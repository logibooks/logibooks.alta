[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $NoRestore,
    [string] $CompilerPath,
    [string] $PublishDirectory,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$serviceProject = Join-Path $repositoryRoot 'Logibooks.Alta.Service\Logibooks.Alta.Service.csproj'
$setupDirectory = Join-Path $repositoryRoot 'Logibooks.Alta.Setup'

& (Join-Path $PSScriptRoot 'Test-Version.ps1')
$props = [xml] [System.IO.File]::ReadAllText((Join-Path $repositoryRoot 'Directory.Build.props'))
$version = $props.Project.PropertyGroup.Version

if (-not $CompilerPath) {
    $candidates = @(
        (Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue).Source
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $CompilerPath = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
    if (-not $CompilerPath) {
        throw 'Inno Setup 6 compiler was not found. Install Inno Setup 6 or pass -CompilerPath.'
    }
}
$compiler = (Resolve-Path -LiteralPath $CompilerPath).Path

if (-not $PublishDirectory) {
    $PublishDirectory = Join-Path $repositoryRoot "Logibooks.Alta.Service\bin\$Configuration\net10.0\win-x64\publish"
    $publishArguments = @(
        'publish', $serviceProject, '--configuration', $Configuration, '--runtime', 'win-x64',
        '--self-contained', 'true', '-p:PublishProfile=win-x64', '--output', $PublishDirectory
    )
    if ($NoRestore) {
        $publishArguments += '--no-restore'
    }
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Self-contained adapter publish failed with exit code $LASTEXITCODE."
    }
}
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$adapter = Join-Path $PublishDirectory 'Logibooks.Alta.Adapter.exe'
$publishedVersion = & $adapter /Version
if ($LASTEXITCODE -ne 0 -or $publishedVersion -ne $version -or
    -not (Test-Path -LiteralPath (Join-Path $PublishDirectory 'coreclr.dll') -PathType Leaf)) {
    throw "Expected a self-contained Windows publish of Alta $version."
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $setupDirectory $Configuration
}
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$setupScript = Join-Path $setupDirectory 'Logibooks.Alta.Setup.iss'
& $compiler "/DAppVersion=$version" "/DPublishDir=$PublishDirectory" "/O$OutputDirectory" $setupScript
if ($LASTEXITCODE -ne 0) {
    throw "Setup EXE build failed with exit code $LASTEXITCODE."
}

$installer = Get-Item -LiteralPath (Join-Path $OutputDirectory 'Logibooks.Alta.Adapter.Setup.exe')
$fileVersion = $installer.VersionInfo
$actualVersion = "$($fileVersion.FileMajorPart).$($fileVersion.FileMinorPart).$($fileVersion.FileBuildPart).$($fileVersion.FilePrivatePart)"
if ($actualVersion -ne "$version.0") {
    throw "Setup EXE file version must match $version.0."
}
Write-Host "Built $($installer.FullName)"
