[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$vswhereCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'),
    (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\Installer\vswhere.exe')
)
$vswhere = $vswhereCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $vswhere) {
    throw 'Visual Studio locator (vswhere.exe) was not found.'
}

$vsixInstaller = & $vswhere -latest -products '*' -find 'Common7\IDE\VSIXInstaller.exe' | Select-Object -First 1
if (-not $vsixInstaller) {
    throw 'VSIXInstaller.exe was not found.'
}
$devenv = & $vswhere -latest -products '*' -find 'Common7\IDE\devenv.com' | Select-Object -First 1
if (-not $devenv) {
    throw 'devenv.com was not found.'
}

$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$vsix = Join-Path $tempRoot 'MicrosoftVisualStudioInstallerProjects.vsix'

$uri = 'https://marketplace.visualstudio.com/_apis/public/gallery/publishers/VisualStudioClient/vsextensions/MicrosoftVisualStudio2022InstallerProjects/latest/vspackage'
Invoke-WebRequest -UseBasicParsing $uri -OutFile $vsix
$process = Start-Process -FilePath $vsixInstaller -ArgumentList '/quiet', '/admin', $vsix -Wait -PassThru
if ($process.ExitCode -ne 0) {
    throw "Visual Studio Installer Projects extension installation failed with exit code $($process.ExitCode)."
}

& $devenv /UpdateConfiguration
if ($LASTEXITCODE -ne 0) {
    throw "Visual Studio extension registration failed with exit code $LASTEXITCODE."
}

Write-Host 'Microsoft Visual Studio Installer Projects 3.x is available.'
