[CmdletBinding()]
param(
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$environmentPath = Join-Path $repositoryRoot '.env'
$backupDefault = 'D:\Projects\logibooks\AltaDB'
$dataDefault = 'D:\Projects\logibooks\AltaInstance3'

function Read-EnvironmentValue([string] $Name, [string] $DefaultValue) {
    if (-not (Test-Path -LiteralPath $environmentPath -PathType Leaf)) {
        return $DefaultValue
    }

    $prefix = "$Name="
    $line = Get-Content -LiteralPath $environmentPath |
        Where-Object { $_.StartsWith($prefix, [System.StringComparison]::Ordinal) } |
        Select-Object -Last 1
    if (-not $line) {
        return $DefaultValue
    }

    $line.Substring($prefix.Length).Trim().Trim('"').Replace('/', '\')
}

function Test-PathWithin([string] $Candidate, [string] $Container) {
    $candidatePath = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\')
    $containerPath = [System.IO.Path]::GetFullPath($Container).TrimEnd('\')
    $candidatePath.Equals($containerPath, [System.StringComparison]::OrdinalIgnoreCase) -or
        $candidatePath.StartsWith($containerPath + '\', [System.StringComparison]::OrdinalIgnoreCase)
}

$dataDirectory = [System.IO.Path]::GetFullPath((Read-EnvironmentValue 'ALTA_INSTANCE3_DIR' $dataDefault)).TrimEnd('\')
$backupDirectory = [System.IO.Path]::GetFullPath((Read-EnvironmentValue 'ALTA_BACKUP_DIR' $backupDefault)).TrimEnd('\')
$dataRoot = [System.IO.Path]::GetPathRoot($dataDirectory).TrimEnd('\')

if ([string]::IsNullOrWhiteSpace($dataDirectory) -or $dataDirectory -eq $dataRoot) {
    throw 'Refusing to reset an empty path or drive root.'
}
if ([System.IO.Path]::GetFileName($dataDirectory) -ne 'AltaInstance3') {
    throw "Refusing unexpected data directory '$dataDirectory'; its final segment must be AltaInstance3."
}
if ((Test-PathWithin $dataDirectory $repositoryRoot) -or (Test-PathWithin $repositoryRoot $dataDirectory)) {
    throw 'Refusing to reset the repository or any directory containing it.'
}
if ((Test-PathWithin $dataDirectory $backupDirectory) -or (Test-PathWithin $backupDirectory $dataDirectory)) {
    throw 'Refusing to reset the backup directory or any directory containing it.'
}

if (-not $Force) {
    $confirmation = Read-Host "Type RESET to stop Compose and clear only '$dataDirectory'"
    if ($confirmation -cne 'RESET') {
        Write-Host 'Reset cancelled.'
        return
    }
}

Push-Location $repositoryRoot
try {
    & docker compose down
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose down failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

if (Test-Path -LiteralPath $dataDirectory -PathType Container) {
    Get-ChildItem -LiteralPath $dataDirectory -Force | Remove-Item -Recurse -Force
}
else {
    [System.IO.Directory]::CreateDirectory($dataDirectory) | Out-Null
}

Write-Host "Cleared development SQL data directory: $dataDirectory"
Write-Host 'Run scripts/Start-Development.ps1 for one clean restore.'

