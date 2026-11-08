[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $NoRestore
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$serviceProject = Join-Path $repositoryRoot 'Logibooks.Alta.Service\Logibooks.Alta.Service.csproj'
$solution = Join-Path $repositoryRoot 'Logibooks.Alta.sln'
$setupProject = Join-Path $repositoryRoot 'Logibooks.Alta.Setup\Logibooks.Alta.Setup.vdproj'

& (Join-Path $PSScriptRoot 'Test-Version.ps1')

if (-not $NoRestore) {
    & dotnet restore $serviceProject --runtime win-x64
    if ($LASTEXITCODE -ne 0) {
        throw "win-x64 restore failed with exit code $LASTEXITCODE."
    }
}

& dotnet publish $serviceProject --configuration $Configuration --runtime win-x64 --self-contained true --no-restore -p:PublishProfile=win-x64
if ($LASTEXITCODE -ne 0) {
    throw "Self-contained service publish failed with exit code $LASTEXITCODE."
}

$vswhereCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'),
    (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\Installer\vswhere.exe')
)
$vswhere = $vswhereCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $vswhere) {
    throw 'Visual Studio locator (vswhere.exe) was not found.'
}

$devenv = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'Common7\IDE\devenv.com' | Select-Object -First 1
if (-not $devenv) {
    throw 'A compatible Visual Studio installation was not found.'
}

& $devenv $solution /Build "$Configuration|Any CPU" /Project $setupProject
if ($LASTEXITCODE -ne 0) {
    throw "MSI build failed with exit code $LASTEXITCODE. Install Microsoft Visual Studio Installer Projects 3.x."
}

$msi = Join-Path (Split-Path -Parent $setupProject) "$Configuration\Logibooks.Alta.Adapter.Setup.msi"
if (-not (Test-Path $msi)) {
    throw "Visual Studio completed without producing '$msi'."
}

Write-Host "Built $msi"
