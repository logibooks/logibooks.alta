[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw "Version '$Version' is invalid. Expected X.Y.Z with non-negative integers and no leading zeroes."
}

$major = [int] $Matches[1]
$minor = [int] $Matches[2]
$patch = [int] $Matches[3]
if ($major -gt 255 -or $minor -gt 255 -or $patch -gt 65535) {
    throw "Version '$Version' exceeds Windows Installer limits (major/minor <= 255, patch <= 65535)."
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repositoryRoot 'Directory.Build.props'
$versionInfoPath = Join-Path $repositoryRoot 'Logibooks.Alta.Service\VersionInfo.cs'
$setupPath = Join-Path $repositoryRoot 'Logibooks.Alta.Setup\Logibooks.Alta.Setup.vdproj'
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Update-TextFile {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [scriptblock] $Transform
    )

    $original = [System.IO.File]::ReadAllText($Path)
    $updated = & $Transform $original
    if ($updated -eq $original) {
        throw "No version update was made in '$Path'."
    }

    [System.IO.File]::WriteAllText($Path, $updated, $utf8)
}

Update-TextFile $propsPath {
    param($text)
    $text = $text -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>"
    $text = $text -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$Version.0</AssemblyVersion>"
    $text = $text -replace '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$Version.0</FileVersion>"
    $text -replace '<InformationalVersion>[^<]+</InformationalVersion>', "<InformationalVersion>$Version</InformationalVersion>"
}

Update-TextFile $versionInfoPath {
    param($text)
    $text -replace 'AppVersion = "[^"]+"', "AppVersion = `"$Version`""
}

$productCode = ([guid]::NewGuid()).ToString('B').ToUpperInvariant()
$packageCode = ([guid]::NewGuid()).ToString('B').ToUpperInvariant()
Update-TextFile $setupPath {
    param($text)
    $text = $text -replace '"ProductVersion" = "8:[^"]+"', "`"ProductVersion`" = `"8:$Version`""
    $text = $text -replace '"ProductCode" = "8:\{[^}]+\}"', "`"ProductCode`" = `"8:$productCode`""
    $text -replace '"PackageCode" = "8:\{[^}]+\}"', "`"PackageCode`" = `"8:$packageCode`""
}

& (Join-Path $PSScriptRoot 'Test-Version.ps1') -ExpectedVersion $Version
Write-Host "Updated Logibooks Alta Adapter to $Version. ProductCode and PackageCode were regenerated; UpgradeCode was preserved."
