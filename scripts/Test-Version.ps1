[CmdletBinding()]
param(
    [string] $ExpectedVersion,
    [string] $ExpectedTag
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repositoryRoot 'Directory.Build.props'

$props = [xml] [System.IO.File]::ReadAllText($propsPath)
$versionNodes = $props.SelectNodes('/Project/PropertyGroup/Version')
if ($versionNodes.Count -ne 1 -or
    $versionNodes[0].InnerText -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z') {
    throw "Expected one Version property in X.Y.Z format in '$propsPath'."
}
$actualVersion = $versionNodes[0].InnerText

if ($ExpectedVersion -and $ExpectedVersion -cne $actualVersion) {
    throw "Expected version '$ExpectedVersion', but checked-in version is '$actualVersion'."
}

if ($ExpectedTag -and $ExpectedTag -cne "v$actualVersion") {
    throw "Release tag '$ExpectedTag' must match checked-in version 'v$actualVersion'."
}

Write-Host "Version consistency check passed: $actualVersion"
