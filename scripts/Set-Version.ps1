[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$parsedVersion = $null
if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z' -or
    -not [version]::TryParse($Version, [ref] $parsedVersion)) {
    throw "Version '$Version' is invalid. Expected X.Y.Z with non-negative integers and no leading zeroes."
}

if ($parsedVersion.Major -gt 65535 -or $parsedVersion.Minor -gt 65535 -or $parsedVersion.Build -gt 65535) {
    throw "Version '$Version' exceeds the Windows file version limit of 65535 per component."
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repositoryRoot 'Directory.Build.props'
$utf8 = [System.Text.UTF8Encoding]::new($false)

$props = [System.IO.File]::ReadAllText($propsPath)
$projectVersionPattern = '<Version>[^<]+</Version>'
if ([regex]::Matches($props, $projectVersionPattern).Count -ne 1) {
    throw "Expected one Version property in '$propsPath'."
}
$updatedProps = $props -creplace $projectVersionPattern, "<Version>$Version</Version>"

if ($updatedProps -cne $props) {
    [System.IO.File]::WriteAllText($propsPath, $updatedProps, $utf8)
}

& (Join-Path $PSScriptRoot 'Test-Version.ps1') -ExpectedVersion $Version
