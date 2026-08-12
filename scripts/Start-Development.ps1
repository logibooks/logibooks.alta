[CmdletBinding()]
param(
    [string] $BackupDirectory = 'D:\Projects\logibooks\AltaDB',
    [string] $DataDirectory = 'D:\Projects\logibooks\AltaInstance3',
    [ValidateRange(1, 65535)] [int] $SqlPort = 1433,
    [ValidateRange(1, 65535)] [int] $AdminerPort = 8083
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$environmentPath = Join-Path $repositoryRoot '.env'

function New-DevelopmentPassword {
    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@%+=_'
    $characters = [System.Collections.Generic.List[char]]::new()
    foreach ($character in 'Aa7!'.ToCharArray()) {
        $characters.Add($character)
    }
    while ($characters.Count -lt 32) {
        $characters.Add($alphabet[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($alphabet.Length)])
    }
    for ($index = $characters.Count - 1; $index -gt 0; $index--) {
        $swapIndex = [System.Security.Cryptography.RandomNumberGenerator]::GetInt32($index + 1)
        ($characters[$index], $characters[$swapIndex]) = ($characters[$swapIndex], $characters[$index])
    }
    -join $characters
}

function Convert-ToComposePath([string] $Path) {
    [System.IO.Path]::GetFullPath($Path).Replace('\', '/')
}

function Read-EnvironmentValue([string] $Name, [string] $DefaultValue) {
    $prefix = "$Name="
    $line = Get-Content -LiteralPath $environmentPath |
        Where-Object { $_.StartsWith($prefix, [System.StringComparison]::Ordinal) } |
        Select-Object -Last 1
    if (-not $line) {
        return $DefaultValue
    }

    $line.Substring($prefix.Length).Trim().Trim('"')
}

$environmentExists = Test-Path -LiteralPath $environmentPath -PathType Leaf
if ($environmentExists) {
    $BackupDirectory = (Read-EnvironmentValue 'ALTA_BACKUP_DIR' $BackupDirectory).Replace('/', '\')
    $DataDirectory = (Read-EnvironmentValue 'ALTA_INSTANCE3_DIR' $DataDirectory).Replace('/', '\')
    $configuredSqlPort = Read-EnvironmentValue 'MSSQL_PORT' $SqlPort
    $configuredAdminerPort = Read-EnvironmentValue 'ADMINER_PORT' $AdminerPort
    if (-not [int]::TryParse($configuredSqlPort, [ref] $SqlPort) -or
        $SqlPort -lt 1 -or $SqlPort -gt 65535) {
        throw "MSSQL_PORT in '$environmentPath' must be between 1 and 65535."
    }
    if (-not [int]::TryParse($configuredAdminerPort, [ref] $AdminerPort) -or
        $AdminerPort -lt 1 -or $AdminerPort -gt 65535) {
        throw "ADMINER_PORT in '$environmentPath' must be between 1 and 65535."
    }
}

$backupPath = Join-Path $BackupDirectory 'gtk_2026-07-30.bak'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker CLI was not found. Install and start Docker Desktop before running this helper.'
}

& docker info --format '{{.ServerVersion}}' *> $null
if ($LASTEXITCODE -ne 0) {
    throw 'Docker Desktop is not running or its Linux container engine is unavailable.'
}

if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
    throw "Required development backup was not found: $backupPath"
}

[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetFullPath($DataDirectory)) | Out-Null

if (-not $environmentExists) {
    $lines = @(
        "MSSQL_SA_PASSWORD=$(New-DevelopmentPassword)",
        "ALTA_DB_PASSWORD=$(New-DevelopmentPassword)",
        "ALTA_BACKUP_DIR=$(Convert-ToComposePath $BackupDirectory)",
        "ALTA_INSTANCE3_DIR=$(Convert-ToComposePath $DataDirectory)",
        "MSSQL_PORT=$SqlPort",
        "ADMINER_PORT=$AdminerPort",
        'ALTA_VERSION=0.2.0'
    )
    [System.IO.File]::WriteAllLines($environmentPath, $lines, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Created ignored development settings at $environmentPath."
}
else {
    Write-Host "Using existing development settings at $environmentPath."
}

Push-Location $repositoryRoot
try {
    & docker compose up --build --detach
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose up failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

Write-Host "Logibooks Alta development services started. Adminer: http://localhost:$AdminerPort"
Write-Host 'Follow adapter logs with: docker compose logs --follow adapter'
