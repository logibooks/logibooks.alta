#Requires -Version 7.3

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $BackupPath,
    [string] $SqlPackage = 'SqlPackage',
    [string] $SchemaSqlPath,
    [ValidateRange(1024, 65535)][int] $ScratchPort = 11433
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$tool = Join-Path $repositoryRoot 'Logibooks.Alta.DevTool/Logibooks.Alta.DevTool.csproj'
$image = 'mcr.microsoft.com/mssql/server:2025-CU7-ubuntu-24.04'
$sourceOutput = & dotnet run --project $tool --configuration Release --verbosity quiet -- inspect-source
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the restored source reestr_1 database.' }
$source = $sourceOutput | Select-Object -Last 1 | ConvertFrom-Json
$collation = $source.collation
$compatibility = $source.compatibility

$backup = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($BackupPath)
$manifest = "$backup.manifest.json"
if ((Test-Path -LiteralPath $backup) -or (Test-Path -LiteralPath $manifest)) {
    throw 'Choose a new path for the backup and manifest.'
}
$package = if (-not $SchemaSqlPath) { (Get-Command $SqlPackage -CommandType Application).Source }
$sourceScript = if ($SchemaSqlPath) { (Resolve-Path -LiteralPath $SchemaSqlPath).Path }
$scratchName = 'alta-baseline-' + [guid]::NewGuid().ToString('N')
$scratchPath = Join-Path (Split-Path -Parent $backup) $scratchName
$null = New-Item -ItemType Directory -Path $scratchPath -Force
$dacpac = Join-Path $scratchPath 'reestr_1.dacpac'
$deployment = Join-Path $scratchPath 'deployment.sql'
$scratchBackup = Join-Path $scratchPath 'reestr_1.bak'
$scratchManifest = "$scratchBackup.manifest.json"
$serverBackup = '/var/opt/mssql/data/alta-development-baseline.bak'
$oldSaPassword = $env:MSSQL_SA_PASSWORD
$oldScratchConnection = $env:ALTA_BASELINE_SCRATCH_CONNECTION_STRING
$scratchPassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)) + 'a9!'
$started = $false

try {
    if ($package) {
        $extract = @('/Action:Extract', "/SourceConnectionString:$env:ALTA_SOURCE_CONNECTION_STRING", "/TargetFile:$dacpac",
            '/p:ExtractAllTableData=False', '/p:VerifyExtraction=True', '/p:ExtractReferencedServerScopedElements=False',
            '/p:IgnoreUserLoginMappings=True')
        & $package @extract
        if ($LASTEXITCODE -ne 0) { throw 'SQLPackage schema extraction failed.' }
    }

    $env:MSSQL_SA_PASSWORD = $scratchPassword
    $create = @('run', '--detach', '--rm', '--name', $scratchName, '--publish', ("127.0.0.1:{0}:1433" -f $ScratchPort),
        '--env', 'MSSQL_SA_PASSWORD', '--env', 'ACCEPT_EULA=Y', '--env', 'MSSQL_PID=EnterpriseDeveloper', $image)
    & docker @create | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start the scratch SQL Server.' }
    $started = $true

    $sqlcmd = 'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" exec /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -l 2'
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    do {
        'SELECT 1;' | & docker exec -i $scratchName bash -c $sqlcmd 2>$null | Out-Null
        $ready = $LASTEXITCODE -eq 0
        if (-not $ready) { Start-Sleep -Seconds 2 }
    } until ($ready -or [DateTime]::UtcNow -ge $deadline)
    if (-not $ready) { throw 'Scratch SQL Server did not become ready.' }

    "CREATE DATABASE [reestr_1] COLLATE $collation;" | & docker exec -i $scratchName bash -c $sqlcmd
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the scratch database.' }
    "ALTER DATABASE [reestr_1] SET COMPATIBILITY_LEVEL = $compatibility;" | & docker exec -i $scratchName bash -c $sqlcmd
    if ($LASTEXITCODE -ne 0) { throw 'Could not set scratch compatibility level.' }

    $target = [System.Data.Common.DbConnectionStringBuilder]::new()
    $target['Server'] = "tcp:127.0.0.1,$ScratchPort"
    $target['Database'] = 'reestr_1'
    $target['User ID'] = 'sa'
    $target['Password'] = $scratchPassword
    $target['Encrypt'] = 'True'
    $target['TrustServerCertificate'] = 'True'
    $env:ALTA_BASELINE_SCRATCH_CONNECTION_STRING = $target.ConnectionString

    if ($package) {
        $script = @('/Action:Script', "/SourceFile:$dacpac", "/TargetConnectionString:$env:ALTA_BASELINE_SCRATCH_CONNECTION_STRING",
            "/OutputPath:$deployment",
            '/p:ScriptDatabaseOptions=False', '/p:IgnoreFileAndLogFilePath=True', '/p:IgnoreFileSize=True',
            '/p:ExcludeObjectTypes=Users;Logins;RoleMembership;Permissions;DatabaseScopedCredentials;Files;Filegroups')
        & $package @script
        if ($LASTEXITCODE -ne 0) { throw 'SQLPackage could not script the verified schema for scratch.' }

        # The pinned Linux image cannot create the source Docs full-text index.
        $schema = Get-Content -LiteralPath $deployment -Raw
        $schema = [regex]::Replace($schema, '(?ims)^[ \t]*CREATE\s+FULLTEXT\s+INDEX\s+ON\s+\[dbo\]\.\[Docs\]\s+.*?;[ \t]*\r?$', '')
        [IO.File]::WriteAllText($deployment, $schema)
        $sourceScript = $deployment
    }

    $deploymentOutput = Get-Content -LiteralPath $sourceScript -Raw |
        & docker exec -i $scratchName bash -c "$sqlcmd -d reestr_1 -f 65001"
    if ($LASTEXITCODE -ne 0) {
        $deploymentOutput | Select-Object -Last 20 | Write-Host
        throw 'The schema script failed in scratch.'
    }

    & dotnet run --project $tool --configuration Release -- prepare-baseline $serverBackup
    if ($LASTEXITCODE -ne 0) { throw 'Could not back up the compact development database.' }
    & docker cp ('{0}:{1}' -f $scratchName, $serverBackup) $scratchBackup
    if ($LASTEXITCODE -ne 0) { throw 'Could not copy the compact backup out of scratch.' }
    & dotnet run --project $tool --configuration Release -- write-manifest $serverBackup $scratchBackup
    if ($LASTEXITCODE -ne 0) { throw 'Could not write the compact backup manifest.' }
    Move-Item -LiteralPath $scratchBackup -Destination $backup
    Move-Item -LiteralPath $scratchManifest -Destination $manifest
    Write-Host "Baseline: $backup"
    Write-Host "Manifest: $manifest"
}
finally {
    if ($started) {
        & docker stop $scratchName | Out-Null
    }
    $env:MSSQL_SA_PASSWORD = $oldSaPassword
    $env:ALTA_BASELINE_SCRATCH_CONNECTION_STRING = $oldScratchConnection
    Remove-Item -LiteralPath $dacpac, $deployment, $scratchBackup, $scratchManifest -ErrorAction SilentlyContinue
    if (-not (Get-ChildItem -LiteralPath $scratchPath -Force)) {
        Remove-Item -LiteralPath $scratchPath
    }
}
