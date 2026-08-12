[CmdletBinding()]
param(
    [string] $ExpectedVersion,
    [string] $ExpectedTag
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Read-Match {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Pattern,
        [Parameter(Mandatory)] [string] $Label
    )

    $text = [System.IO.File]::ReadAllText($Path)
    $match = [regex]::Match($text, $Pattern)
    if (-not $match.Success) {
        throw "Could not read $Label from '$Path'."
    }

    $match.Groups[1].Value
}

$propsPath = Join-Path $repositoryRoot 'Directory.Build.props'
$versionInfoPath = Join-Path $repositoryRoot 'Logibooks.Alta.Service\VersionInfo.cs'
$setupPath = Join-Path $repositoryRoot 'Logibooks.Alta.Setup\Logibooks.Alta.Setup.vdproj'
$adapterDockerfilePath = Join-Path $repositoryRoot 'docker\adapter\Dockerfile'
$composePath = Join-Path $repositoryRoot 'docker-compose.yml'
$environmentExamplePath = Join-Path $repositoryRoot '.env.example'
$startDevelopmentPath = Join-Path $repositoryRoot 'scripts\Start-Development.ps1'

$versions = [ordered]@{
    Project = Read-Match $propsPath '<Version>([^<]+)</Version>' 'project version'
    Informational = Read-Match $propsPath '<InformationalVersion>([^<]+)</InformationalVersion>' 'informational version'
    VersionInfo = Read-Match $versionInfoPath 'AppVersion = "([^"]+)"' 'VersionInfo.AppVersion'
    Installer = Read-Match $setupPath '"ProductVersion" = "8:([^"]+)"' 'MSI ProductVersion'
    AdapterImage = Read-Match $adapterDockerfilePath 'ARG VERSION=(\d+\.\d+\.\d+)' 'adapter image version'
    Compose = Read-Match $composePath 'ALTA_VERSION:-(\d+\.\d+\.\d+)' 'Compose version'
    EnvironmentExample = Read-Match $environmentExamplePath 'ALTA_VERSION=(\d+\.\d+\.\d+)' '.env.example version'
    DevelopmentHelper = Read-Match $startDevelopmentPath 'ALTA_VERSION=(\d+\.\d+\.\d+)' 'development helper version'
}

$dockerfileVersions = [regex]::Matches(
    [System.IO.File]::ReadAllText($adapterDockerfilePath),
    'ARG VERSION=(\d+\.\d+\.\d+)') | ForEach-Object { $_.Groups[1].Value }
if ($dockerfileVersions.Count -ne 2 -or @($dockerfileVersions | Select-Object -Unique).Count -ne 1) {
    throw 'The adapter Dockerfile must contain two matching VERSION build arguments.'
}

$distinctVersions = @($versions.Values | Select-Object -Unique)
if ($distinctVersions.Count -ne 1) {
    throw "Version mismatch: $($versions | ConvertTo-Json -Compress)"
}

$actualVersion = $distinctVersions[0]
if ($ExpectedVersion -and $ExpectedVersion -ne $actualVersion) {
    throw "Expected version '$ExpectedVersion', but checked-in version is '$actualVersion'."
}

if ($ExpectedTag) {
    if ($ExpectedTag -notmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
        throw "Release tag '$ExpectedTag' is invalid. Expected vX.Y.Z."
    }

    $tagVersion = $ExpectedTag.Substring(1)
    if ($tagVersion -ne $actualVersion) {
        throw "Release tag '$ExpectedTag' does not match checked-in version '$actualVersion'."
    }
}

Write-Host "Version consistency check passed: $actualVersion"
