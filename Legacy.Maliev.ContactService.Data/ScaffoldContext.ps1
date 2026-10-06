[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$connection = [Environment]::GetEnvironmentVariable('ConnectionStrings__ContactRequestDbContext', 'Process')
if ([string]::IsNullOrWhiteSpace($connection)) {
    throw 'Required external Contact connection setting is not configured.'
}

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (![IO.Path]::IsPathFullyQualified($OutputDirectory)) {
    throw 'Scaffold output must be an absolute directory path.'
}
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if ($destination.Equals($repository, [StringComparison]::OrdinalIgnoreCase) -or
    $destination.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Scaffold output must be a new directory outside the repository.'
}
if (Test-Path -LiteralPath $destination) {
    throw 'Scaffold output already exists; existing files are preserved.'
}

# Reject redirected ancestors before giving the external tool an output path.
$ancestor = [IO.DirectoryInfo]::new($destination).Parent
while ($null -ne $ancestor) {
    if (Test-Path -LiteralPath $ancestor.FullName) {
        $item = Get-Item -LiteralPath $ancestor.FullName -Force
        if (!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Scaffold output ancestor is not an ordinary directory.'
        }
    }
    $ancestor = $ancestor.Parent
}

# The named setting avoids exposing its value in the process command line.
# No OnConfiguring override, resource replacement, build, or force overwrite.
$arguments = @(
    'ef', 'dbcontext', 'scaffold', 'Name=ConnectionStrings:ContactRequestDbContext',
    'Npgsql.EntityFrameworkCore.PostgreSQL',
    '--project', (Join-Path $PSScriptRoot 'Legacy.Maliev.ContactService.Data.csproj'),
    '--startup-project', (Join-Path $repository 'Legacy.Maliev.ContactService.Api/Legacy.Maliev.ContactService.Api.csproj'),
    '--context', 'ContactScaffoldContext',
    '--namespace', 'Legacy.Maliev.ContactService.ScaffoldPreview',
    '--context-namespace', 'Legacy.Maliev.ContactService.ScaffoldPreview',
    '--output-dir', $destination, '--context-dir', $destination,
    '--configuration', 'Release', '--table', 'Message', '--no-onconfiguring', '--no-build'
)
try {
    $null = & dotnet @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'External CLI failed.' }
} catch {
    throw [InvalidOperationException]::new('Contact scaffold generation failed; details withheld.')
}

Write-Output 'Contact scaffold preview generated.'
