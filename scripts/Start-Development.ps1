[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$configurationPath = Join-Path $repositoryRoot 'Logibooks.Alta.Service\appsettings.Development.local.json'
$databaseName = 'reestr_1'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker CLI was not found. Install and start Docker Desktop before running this helper.'
}

$engine = & docker info --format '{{.OSType}}'
if ($LASTEXITCODE -ne 0 -or $engine -ne 'linux') {
    throw 'Start Docker Desktop with the Linux container engine.'
}

Push-Location $repositoryRoot
try {
    $modelJson = & docker compose config --format json
    if ($LASTEXITCODE -ne 0) {
        throw 'Set the existing SQL Server sa password in .env. See .env.example.'
    }

    $passwordPrefix = 'ALTA_DB_PASSWORD='
    $composeEnvironment = & docker compose config --environment
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not read the Compose environment.'
    }
    $passwordEntry = $composeEnvironment |
        Where-Object { $_.StartsWith($passwordPrefix, [StringComparison]::Ordinal) } |
        Select-Object -First 1
    if (-not $passwordEntry -or $passwordEntry.Length -eq $passwordPrefix.Length) {
        throw 'Set ALTA_DB_PASSWORD in .env to the read-only logibooks SQL login password.'
    }

    $model = $modelJson | Out-String | ConvertFrom-Json
    $sql = $model.services.mssql
    $databaseMount = $sql.volumes | Where-Object target -eq '/database'
    foreach ($fileName in 'reestr_1_1.mdf', 'reestr_1_1_log.ldf') {
        $filePath = Join-Path $databaseMount.source $fileName
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "Restored database file is missing: $filePath"
        }
    }

    & docker compose up --detach --wait --wait-timeout 360
    if ($LASTEXITCODE -ne 0) {
        throw "Docker Compose did not become ready. Check docker compose logs mssql adminer."
    }

    $sqlPort = ($sql.ports | Where-Object target -eq 1433).published
    $adminerPort = ($model.services.adminer.ports | Where-Object target -eq 8080).published
    $connection = [System.Data.Common.DbConnectionStringBuilder]::new()
    $connection['Server'] = "tcp:127.0.0.1,$sqlPort"
    $connection['Database'] = $databaseName
    $connection['User ID'] = 'logibooks'
    $connection['Password'] = $passwordEntry.Substring($passwordPrefix.Length)
    $connection['Encrypt'] = 'True'
    $connection['TrustServerCertificate'] = 'True'

    if (Test-Path -LiteralPath $configurationPath -PathType Leaf) {
        $configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
    }
    else {
        $configuration = [pscustomobject]@{
            Service = @{ Enabled = $true }
        }
    }

    if ($null -eq $configuration.Databases) {
        $configuration | Add-Member -NotePropertyName Databases -NotePropertyValue ([pscustomobject]@{ Targets = @() }) -Force
    }
    $targets = @($configuration.Databases.Targets | Where-Object { $null -ne $_ })
    $target = $targets | Where-Object Name -eq $databaseName | Select-Object -First 1
    if ($null -eq $target) {
        $target = [pscustomobject]@{ Name = $databaseName; Enabled = $true }
        $configuration.Databases | Add-Member -NotePropertyName Targets -NotePropertyValue ($targets + $target) -Force
    }
    $target | Add-Member -NotePropertyName ConnectionString -NotePropertyValue $connection.ConnectionString -Force

    if ($null -eq $configuration.Core) {
        $configuration | Add-Member -NotePropertyName Core -NotePropertyValue ([pscustomobject]@{ Enabled = $false }) -Force
    }
    if ([string]::IsNullOrWhiteSpace($configuration.Core.BaseUrl)) {
        $configuration.Core | Add-Member -NotePropertyName BaseUrl -NotePropertyValue 'http://localhost' -Force
    }
    $configuration | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $configurationPath -Encoding utf8
}
finally {
    Pop-Location
}

Write-Host "SQL Server and Adminer are ready. Adminer is available at http://127.0.0.1:$adminerPort."
Write-Host 'Run Logibooks.Alta.Service with the Alta (Windows development) launch profile.'
