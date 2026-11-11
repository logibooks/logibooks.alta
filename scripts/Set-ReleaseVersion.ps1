[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Tag,
    [switch] $OnlyIncrease
)

$ErrorActionPreference = 'Stop'
if ($Tag -cnotmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "Release tag '$Tag' must have the format vX.Y.Z."
}

$version = $Tag.Substring(1)
$propsPath = (Resolve-Path -LiteralPath 'Directory.Build.props').Path
$props = [System.Xml.XmlDocument]::new()
$props.PreserveWhitespace = $true
$props.Load($propsPath)
$versionNode = $props.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $versionNode) {
    throw 'Directory.Build.props is missing the Version property.'
}

$currentVersion = $versionNode.InnerText
if ($OnlyIncrease -and [version]$version -lt [version]$currentVersion) {
    Write-Host "The default branch already has a newer version ($currentVersion)."
    return
}

if ($currentVersion -cne $version) {
    $versionNode.InnerText = $version
    $props.Save($propsPath)
}
