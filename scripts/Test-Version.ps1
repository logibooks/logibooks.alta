[CmdletBinding()]
param(
    [string] $ExpectedVersion,
    [string] $ExpectedTag
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repositoryRoot 'Directory.Build.props'
$setupPath = Join-Path $repositoryRoot 'Logibooks.Alta.Setup\Logibooks.Alta.Setup.vdproj'

$props = [xml] [System.IO.File]::ReadAllText($propsPath)
$versionNodes = $props.SelectNodes('/Project/PropertyGroup/Version')
if ($versionNodes.Count -ne 1 -or
    $versionNodes[0].InnerText -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z') {
    throw "Expected one Version property in X.Y.Z format in '$propsPath'."
}
$actualVersion = $versionNodes[0].InnerText

$setup = [System.IO.File]::ReadAllText($setupPath)
$installerVersions = [regex]::Matches($setup, '"ProductVersion" = "8:([^"]+)"')
if ($installerVersions.Count -ne 1 -or $installerVersions[0].Groups[1].Value -cne $actualVersion) {
    throw "MSI ProductVersion must match '$actualVersion'. Run scripts/Set-Version.ps1 $actualVersion."
}

if ($ExpectedVersion -and $ExpectedVersion -cne $actualVersion) {
    throw "Expected version '$ExpectedVersion', but checked-in version is '$actualVersion'."
}

if ($ExpectedTag -and $ExpectedTag -cne "v$actualVersion") {
    throw "Release tag '$ExpectedTag' must match checked-in version 'v$actualVersion'."
}

Write-Host "Version consistency check passed: $actualVersion"
