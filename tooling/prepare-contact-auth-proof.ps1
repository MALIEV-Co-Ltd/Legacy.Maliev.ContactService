[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$boundary = [IO.Path]::GetFullPath((Join-Path $root '.dependencies/contact-auth-proof'))

function Invoke-Bounded([string] $Command, [string[]] $Arguments, [int] $Seconds = 300) {
    $start = [Diagnostics.ProcessStartInfo]::new($Command)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $inherited = @{}
    foreach ($name in @('PATH', 'SystemRoot', 'TEMP', 'TMP', 'HOME', 'USERPROFILE', 'DOTNET_ROOT', 'APPDATA', 'LOCALAPPDATA', 'ProgramFiles', 'ProgramFiles(x86)', 'ProgramData')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($null -ne $value) { $inherited[$name] = $value }
    }
    $start.Environment.Clear()
    foreach ($name in $inherited.Keys) { $start.Environment[$name] = $inherited[$name] }
    $start.Environment['GIT_TERMINAL_PROMPT'] = '0'
    $start.Environment['GCM_INTERACTIVE'] = 'Never'
    $start.Environment['GIT_CONFIG_COUNT'] = '0'
    $start.Environment['GIT_CONFIG_NOSYSTEM'] = '1'
    $start.Environment['GIT_CONFIG_GLOBAL'] = $(if ($IsWindows) { 'NUL' } else { '/dev/null' })
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($Seconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'Pinned proof preparation exceeded its deadline.'
        }
        if ($process.ExitCode -ne 0) { throw "Pinned proof preparation failed with exit $($process.ExitCode); details withheld." }
        $null = $errors.GetAwaiter().GetResult()
        return $output.GetAwaiter().GetResult().Trim()
    } finally { $process.Dispose() }
}

function Pinned-Checkout([string] $Repository, [string] $Directory, [string] $Sha) {
    $path = [IO.Path]::GetFullPath((Join-Path $boundary $Directory))
    if (!$path.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Proof checkout escaped its isolated boundary.'
    }
    if (!(Test-Path -LiteralPath $path)) {
        $null = Invoke-Bounded git @('-c', 'credential.helper=', '-c', 'http.extraheader=', '-c', 'core.longpaths=true', 'clone', '--no-checkout', '--filter=blob:none', "https://github.com/MALIEV-Co-Ltd/$Repository.git", $path)
        $null = Invoke-Bounded git @('-C', $path, '-c', 'credential.helper=', '-c', 'http.extraheader=', 'fetch', '--depth=1', 'origin', $Sha)
        $null = Invoke-Bounded git @('-C', $path, '-c', 'core.longpaths=true', 'checkout', '--detach', $Sha)
    }
    if (!(Test-Path -LiteralPath (Join-Path $path '.git'))) { throw 'Existing proof dependency is not Git; preserved.' }
    $status = Invoke-Bounded git @('-C', $path, 'status', '--porcelain', '--untracked-files=all')
    $head = Invoke-Bounded git @('-C', $path, 'rev-parse', 'HEAD')
    $remote = Invoke-Bounded git @('-C', $path, 'remote', 'get-url', 'origin')
    if ($status -or $head -cne $Sha -or $remote -cne "https://github.com/MALIEV-Co-Ltd/$Repository.git") {
        throw 'Dirty, wrong-pin or wrong-origin proof dependency preserved.'
    }
    return $path
}

$auth = Pinned-Checkout 'Legacy.Maliev.AuthService' 'auth' '8cdb634b3b0abdf18b9b826a0948dbfd98c66ea0'
$runtime = Join-Path $boundary 'auth-runtime'
$null = Pinned-Checkout 'Legacy.Maliev.ServiceDefaults' 'auth-runtime/Legacy.Maliev.ServiceDefaults' '8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3'
$null = Pinned-Checkout 'Legacy.Maliev.CompatibilityContracts' 'auth-runtime/Legacy.Maliev.CompatibilityContracts' '78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7'
$output = Invoke-Bounded dotnet @('build', (Join-Path $auth 'Legacy.Maliev.AuthService.Api/Legacy.Maliev.AuthService.Api.csproj'), '-c', 'Release', '--nologo', '--no-incremental', '-warnaserror', '-p:UseLocalMalievDependencies=true', "-p:MalievWorkspaceRoot=$runtime")
Write-Host $output
if (!(Test-Path -LiteralPath (Join-Path $auth 'Legacy.Maliev.AuthService.Api/bin/Release/net10.0/Legacy.Maliev.AuthService.Api.dll'))) { throw 'Pinned Auth proof binary missing.' }
Write-Host 'Isolated exact-pinned Auth proof built; no database or service started.'
