[CmdletBinding()]
param(
    [string]$ScaffoldScript = (Join-Path $PSScriptRoot '../Legacy.Maliev.ContactService.Data/ScaffoldContext.ps1'),
    [Parameter(Mandatory)][string]$EvidencePath
)
$ErrorActionPreference = 'Stop'
$script:Cases = [Collections.Generic.List[object]]::new()
$script:RecordedCalls = [Collections.Generic.List[object]]::new()
$script:Canary = 'scaffold-canary-' + [Guid]::NewGuid().ToString('N')
$script:BoundaryState = [pscustomobject]@{Mode='success';Canary=$script:Canary;Calls=$script:RecordedCalls}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('contact-scaffold-contract-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $fixture) { throw 'Owned fixture unexpectedly exists.' }
New-Item -ItemType Directory -Path $fixture | Out-Null
$connectionKey = 'ConnectionStrings__ContactRequestDbContext'
$previous = [Environment]::GetEnvironmentVariable($connectionKey, 'Process')
$builder = [System.Data.Common.DbConnectionStringBuilder]::new()
$builder['Host'] = '127.0.0.1'
$builder['Port'] = 1
$builder['Database'] = 'scaffold_fixture'
$builder['Username'] = 'fixture'
$builder['Password'] = $script:Canary
$synthetic = $builder.ConnectionString

function Require([bool]$Condition, [string]$Failure) {
    if (!$Condition) { throw $Failure }
}

function Run-Case([string]$Name, [scriptblock]$Body) {
    $script:RecordedCalls.Clear()
    $script:BoundaryState.Mode = 'success'
    try {
        & $Body
        $script:Cases.Add([ordered]@{name=$Name;passed=$true;sdkStarted=$false;databaseOpened=$false})
    } catch {
        # Assertion descriptions are constant and never contain inputs or provider details.
        $script:Cases.Add([ordered]@{name=$Name;passed=$false;failure=$_.Exception.Message.Replace($script:Canary, '[redacted]');sdkStarted=$false;databaseOpened=$false})
    }
}

$state = $script:BoundaryState
$recorder = {
    $arguments = @($args | ForEach-Object { [string]$_ })
    $state.Calls.Add($arguments)
    if ($state.Mode -eq 'throws') { throw ('Controlled external CLI failure ' + $state.Canary) }
    if ($state.Mode -eq 'nonzero') {
        Write-Output ('Controlled external CLI failure ' + $state.Canary)
        $global:LASTEXITCODE = 7
        return
    }
    $index = [Array]::IndexOf($arguments, '--output-dir')
    if ($index -lt 0 -or $index + 1 -ge $arguments.Count) { throw 'Fixture requires explicit output directory.' }
    $directory = $arguments[$index + 1]
    New-Item -ItemType Directory -Path $directory | Out-Null
    # Emulate only the external tool's generated output; script side effects remain real.
    $generated = 'public partial class ContactScaffoldContext(DbContextOptions<ContactScaffoldContext> options) : DbContext(options);'
    if ($arguments -notcontains '--no-onconfiguring') { $generated += ' protected override void OnConfiguring() { UseNpgsql(configurationValue); }' }
    [IO.File]::WriteAllText((Join-Path $directory 'ContactScaffoldContext.cs'), $generated)
    $global:LASTEXITCODE = 0
}.GetNewClosure()
Set-Item -Path Function:dotnet -Value $recorder

try {
    if (!(Test-Path -LiteralPath $ScaffoldScript -PathType Leaf)) { throw 'Scaffold implementation missing; setup failure, not behavioral RED.' }
    foreach ($missing in @($null, '', ' ', "`t`r`n")) {
        Run-Case ('Missing or blank explicit Contact setting refuses CLI: ' + [string]$script:Cases.Count) {
            [Environment]::SetEnvironmentVariable($connectionKey, $missing, 'Process')
            $destination = Join-Path $fixture ('blank-' + [Guid]::NewGuid().ToString('N'))
            $caught = $null
            try { $null = & $ScaffoldScript -OutputDirectory $destination } catch { $caught = $_ }
            Require ($null -ne $caught) 'Missing setting must fail.'
            Require ($script:RecordedCalls.Count -eq 0) 'Missing setting must refuse the CLI boundary.'
            Require (!(Test-Path -LiteralPath $destination)) 'Refusal must not create output.'
        }
    }

    [Environment]::SetEnvironmentVariable($connectionKey, $synthetic, 'Process')
    Run-Case 'Named Npgsql preview preserves caller options and registered context' {
        $destination = Join-Path $fixture 'preview with spaces'
        $context = Join-Path (Split-Path $ScaffoldScript -Parent) 'ContactRequestDbContext.cs'
        $before = (Get-FileHash -LiteralPath $context).Hash
        $console = @(& $ScaffoldScript -OutputDirectory $destination)
        Require ($script:RecordedCalls.Count -eq 1) 'Exactly one CLI boundary call is required.'
        $arguments = $script:RecordedCalls[0]
        Require ($arguments[3] -eq 'Name=ConnectionStrings:ContactRequestDbContext') 'Pass a named setting, never its value.'
        Require ($arguments[4] -eq 'Npgsql.EntityFrameworkCore.PostgreSQL') 'Only the approved target provider is allowed.'
        Require ($arguments -contains '--no-onconfiguring') 'Generated context must preserve caller-supplied options.'
        Require ($arguments -contains '--no-build') 'Scaffolding must not silently launch a build.'
        Require ($arguments -notcontains '--force') 'Existing output must not be overwritten.'
        $tables = @()
        for ($index = 0; $index -lt $arguments.Count; $index++) {
            if ($arguments[$index] -eq '--table') { $tables += $arguments[$index + 1] }
        }
        Require (($tables -join ',') -ceq 'Message') 'Only the owned Contact Message table may be scaffolded.'
        Require ($arguments[$arguments.IndexOf('--context') + 1] -ceq 'ContactScaffoldContext') 'Preview context must be distinct from registered runtime context.'
        Require (-not (($arguments -join ' ').Contains($script:Canary))) 'Canary must not enter CLI arguments.'
        Require (-not (($console -join ' ').Contains($script:Canary))) 'Canary must not enter console output.'
        $generated = [IO.File]::ReadAllText((Join-Path $destination 'ContactScaffoldContext.cs'))
        Require (!$generated.Contains('Properties.Resources')) 'No deleted resource reference may be introduced.'
        Require (!$generated.Contains('OnConfiguring')) 'No provider override may be introduced.'
        Require (!$generated.Contains($script:Canary)) 'No credential canary may be written to generated output.'
        Require ((Get-FileHash -LiteralPath $context).Hash -ceq $before) 'Registered runtime context must remain unchanged.'
    }

    Run-Case 'Existing external output is preserved and CLI is refused' {
        $destination = Join-Path $fixture 'existing'
        New-Item -ItemType Directory -Path $destination | Out-Null
        $marker = Join-Path $destination 'owner.txt'
        [IO.File]::WriteAllText($marker, 'preserve-owned-output')
        $caught = $null
        try { $null = & $ScaffoldScript -OutputDirectory $destination } catch { $caught = $_ }
        Require ($null -ne $caught) 'Existing output must be refused.'
        Require ($script:RecordedCalls.Count -eq 0) 'Existing output must refuse the CLI boundary.'
        Require ([IO.File]::ReadAllText($marker) -ceq 'preserve-owned-output') 'Existing files must remain unchanged.'
    }

    Run-Case 'Project output is refused before any source write' {
        $destination = Join-Path (Split-Path $ScaffoldScript -Parent) 'scaffold-contract-must-not-exist'
        $caught = $null
        try { $null = & $ScaffoldScript -OutputDirectory $destination } catch { $caught = $_ }
        Require ($null -ne $caught) 'Project output must be refused.'
        Require ($script:RecordedCalls.Count -eq 0) 'Project output must refuse the CLI boundary.'
        Require (!(Test-Path -LiteralPath $destination)) 'No project directory may be created.'
    }

    Run-Case 'Relative output is refused before resolving a different process directory' {
        $script:BoundaryState.Mode = 'nonzero'
        $caught = $null
        try { $null = & $ScaffoldScript -OutputDirectory 'relative-scaffold-preview' } catch { $caught = $_ }
        Require ($null -ne $caught) 'Relative output must be refused.'
        Require ($script:RecordedCalls.Count -eq 0) 'Relative output must refuse the CLI boundary.'
    }

    foreach ($mode in @('nonzero', 'throws')) {
        Run-Case ('External CLI ' + $mode + ' is a redacted failure') {
            $script:BoundaryState.Mode = $mode
            $destination = Join-Path $fixture ('failure-' + $mode)
            $caught = $null
            $console = @()
            try { $console = @(& $ScaffoldScript -OutputDirectory $destination) } catch { $caught = $_ }
            Require ($null -ne $caught) 'External CLI failure must fail the script.'
            Require ($script:RecordedCalls.Count -eq 1) 'Failure must not retry the CLI.'
            Require (-not ($caught.Exception.ToString().Contains($script:Canary))) 'Exception must not disclose the canary.'
            Require (-not (($console -join ' ').Contains($script:Canary))) 'Console must not disclose the canary.'
        }
    }
} finally {
    try {
        [Environment]::SetEnvironmentVariable($connectionKey, $previous, 'Process')
        [ordered]@{observedUtc=[DateTimeOffset]::UtcNow;scope='Actual PowerShell orchestration, recorded external CLI boundary; no EF/database execution';passed=@($script:Cases | Where-Object passed -eq $true).Count;failed=@($script:Cases | Where-Object passed -eq $false).Count;cases=$script:Cases;scaffoldScriptSha256=(Get-FileHash -LiteralPath $ScaffoldScript).Hash} |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $EvidencePath
    } finally {
        $absolute = [IO.Path]::GetFullPath($fixture)
        $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (!$absolute.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($absolute) -notmatch '^contact-scaffold-contract-[a-f0-9]{32}$') { throw 'Owned fixture cleanup boundary refused.' }
        if (Test-Path -LiteralPath $absolute) {
            if ((Get-Item -LiteralPath $absolute -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Owned fixture cleanup boundary refused.' }
            Remove-Item -LiteralPath $absolute -Recurse -Force
        }
        if (Test-Path -LiteralPath $absolute) { throw 'Owned fixture cleanup did not remove the disposable directory.' }
    }
}
if (@($script:Cases | Where-Object passed -eq $false).Count) { exit 1 }
exit 0
