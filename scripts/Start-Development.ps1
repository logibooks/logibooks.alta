[CmdletBinding(SupportsShouldProcess)]
param([switch] $ResetAlta)

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
    $compose = @('compose', '-f', 'docker-compose.yml')
    $modelJson = & docker @compose config --format json
    if ($LASTEXITCODE -ne 0) {
        throw 'Set the development SQL Server credentials in .env. See .env.example.'
    }

    $passwordPrefix = 'ALTA_DB_PASSWORD='
    $composeEnvironment = & docker @compose config --environment
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not read the Compose environment.'
    }
    $passwordEntry = $composeEnvironment |
        Where-Object { $_.StartsWith($passwordPrefix, [StringComparison]::Ordinal) } |
        Select-Object -First 1
    if (-not $passwordEntry -or $passwordEntry.Length -eq $passwordPrefix.Length) {
        throw 'Set ALTA_DB_PASSWORD in .env to the read-only logibooks SQL login password.'
    }
    $writerPrefix = 'ALTA_DEV_WRITER_PASSWORD='
    $writerEntry = $composeEnvironment |
        Where-Object { $_.StartsWith($writerPrefix, [StringComparison]::Ordinal) } |
        Select-Object -First 1
    if (-not $writerEntry -or $writerEntry.Length -eq $writerPrefix.Length) {
        throw 'Set ALTA_DEV_WRITER_PASSWORD in .env to the development importer SQL login password.'
    }

    $model = $modelJson | Out-String | ConvertFrom-Json
    $sql = $model.services.mssql
    $baselineMounts = @($sql.volumes) + @($model.services.'mssql-init'.volumes) |
        Where-Object { $_.target -in '/baseline/reestr_1.bak', '/baseline/reestr_1.bak.manifest.json' }
    foreach ($mount in $baselineMounts) {
        if (-not (Test-Path -LiteralPath $mount.source -PathType Leaf)) {
            throw "Development baseline file is missing: $($mount.source). See New-DevelopmentBaseline.ps1."
        }
    }

    if ($ResetAlta) {
        $selected = & docker @compose ps -q mssql
        if ($LASTEXITCODE -ne 0 -or -not $selected) {
            throw 'Start the development database before resetting it.'
        }
        $container = (& docker inspect $selected | ConvertFrom-Json)[0]
        $project = $model.name
        $volumeName = $model.volumes.'sql-dev-state'.name
        $mount = $container.Mounts | Where-Object Destination -eq '/var/opt/mssql'
        if ($container.Config.Labels.'com.docker.compose.project' -cne $project -or
            $container.Config.Labels.'com.docker.compose.service' -cne 'mssql' -or
            $mount.Type -cne 'volume' -or $mount.Name -cne $volumeName -or -not $volumeName) {
            throw 'The running SQL container is not using the expected development volume.'
        }
        $volume = (& docker volume inspect $volumeName | ConvertFrom-Json)[0]
        if ($volume.Labels.'com.docker.compose.project' -cne $project -or
            $volume.Labels.'com.docker.compose.volume' -cne 'sql-dev-state') {
            throw 'The SQL volume does not belong to this Compose development project.'
        }
        if (-not $PSCmdlet.ShouldProcess($project, "Reset the marked $databaseName development database")) {
            return
        }

        $fullCompose = $compose + @('-f', 'docker-compose.override.yml')
        $runningAlta = & docker @fullCompose ps --status running -q alta
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the development adapter.' }
        if ($runningAlta) {
            & docker @fullCompose stop alta
            if ($LASTEXITCODE -ne 0) { throw 'Could not stop the development adapter.' }
        }
        try {
            & docker @compose run --rm --no-deps mssql-init reset
            if ($LASTEXITCODE -ne 0) { throw 'Could not reset the Alta development database.' }
        }
        finally {
            if ($runningAlta) {
                & docker @fullCompose start alta
                if ($LASTEXITCODE -ne 0) { throw 'The adapter could not restart after the reset attempt.' }
            }
        }
        return
    }

    & docker @compose up --build --detach --wait --wait-timeout 360
    if ($LASTEXITCODE -ne 0) {
        throw 'Docker Compose did not become ready. Check docker compose logs mssql mssql-init adminer.'
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
