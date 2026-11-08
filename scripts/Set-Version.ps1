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

if ($parsedVersion.Major -gt 255 -or $parsedVersion.Minor -gt 255 -or $parsedVersion.Build -gt 65535) {
    throw "Version '$Version' exceeds Windows Installer limits (major/minor <= 255, patch <= 65535)."
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repositoryRoot 'Directory.Build.props'
$setupPath = Join-Path $repositoryRoot 'Logibooks.Alta.Setup\Logibooks.Alta.Setup.vdproj'
$utf8 = [System.Text.UTF8Encoding]::new($false)

$props = [System.IO.File]::ReadAllText($propsPath)
$projectVersionPattern = '<Version>[^<]+</Version>'
if ([regex]::Matches($props, $projectVersionPattern).Count -ne 1) {
    throw "Expected one Version property in '$propsPath'."
}
$updatedProps = $props -creplace $projectVersionPattern, "<Version>$Version</Version>"

$setup = [System.IO.File]::ReadAllText($setupPath)
$installerVersionPattern = '"ProductVersion" = "8:([^"]+)"'
$installerVersions = [regex]::Matches($setup, $installerVersionPattern)
if ($installerVersions.Count -ne 1) {
    throw "Expected one ProductVersion in '$setupPath'."
}

if ($installerVersions[0].Groups[1].Value -cne $Version) {
    $updatedSetup = $setup -creplace $installerVersionPattern, ('"ProductVersion" = "8:{0}"' -f $Version)
    foreach ($field in 'ProductCode', 'PackageCode') {
        $pattern = '"{0}" = "8:\{{[^}}]+\}}"' -f $field
        if ([regex]::Matches($updatedSetup, $pattern).Count -ne 1) {
            throw "Expected one $field in '$setupPath'."
        }

        $code = ([guid]::NewGuid()).ToString('B').ToUpperInvariant()
        $updatedSetup = $updatedSetup -creplace $pattern, ('"{0}" = "8:{1}"' -f $field, $code)
    }

    [System.IO.File]::WriteAllText($setupPath, $updatedSetup, $utf8)
}

if ($updatedProps -cne $props) {
    [System.IO.File]::WriteAllText($propsPath, $updatedProps, $utf8)
}

& (Join-Path $PSScriptRoot 'Test-Version.ps1') -ExpectedVersion $Version
