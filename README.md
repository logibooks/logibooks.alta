# Logibooks Alta Adapter

<img src="Logibooks.Alta.Service/Assets/Logibooks.Alta.Adapter.png" alt="Logibooks Alta Adapter" width="30">

Logibooks Alta Adapter is a Windows application that monitors one or more Microsoft SQL Server databases and communicates with Logibooks Core over authenticated HTTPS.

## Runtime behavior

- The self-contained x64 adapter installs as the `LogibooksAltaAdapter` Windows service and runs under `LocalService`.
- Production SQL Server instances and databases remain external and are not installed or managed by the adapter.
- In development, run the adapter on Windows with SQL Server 2025 and Adminer in Docker Desktop's Linux containers.
- Quartz runs an initial cycle on enabled startup, then follows the configured cron and time zone. A running cycle causes overlapping firings to be skipped.
- With Core enabled, each cycle refreshes the CMR worklist and reads the matching Alta declarations and IndPost parcels. For queued assignments and retries, the adapter reads the declaration XML in `Docs.BLOB` and matches the shipment by IndPost `DOCGID` and parcel number. It sends all goods lines in XML order, using each line's total in the invoice currency. Ambiguous or incomplete assignments stay pending. The adapter keeps the monitoring baseline in memory.
- With Core disabled, enabled SQL targets receive a connectivity probe. Target concurrency is bounded in either mode.
- Transient GET failures receive up to three attempts with 1- and 2-second delays. An explicit HTTP `401` permits one fresh-login retry. A submission with an unknown outcome can be retried during a later eligible cycle.
- Configuration reload applies schedule changes without rebuilding the monitoring baseline. Changing the SQL source, Core URL, or Core account starts a fresh baseline for that scope.
- Enable submissions after Core and the adapter support the array-based Alta batch contract.
- Logs identify database targets by configured name but sanitize connection strings, SQL passwords, Core passwords, and JWTs.

## Build and test

Use the .NET 10 SDK:

```powershell
dotnet restore .\Logibooks.Alta.Tests\Logibooks.Alta.Tests.csproj
dotnet test .\Logibooks.Alta.Tests\Logibooks.Alta.Tests.csproj --configuration Release
dotnet publish .\Logibooks.Alta.Service\Logibooks.Alta.Service.csproj `
  --configuration Release --runtime win-x64 --self-contained true `
  -p:PublishProfile=win-x64
```

Build the Windows setup EXE with Inno Setup 6.3 or later in the 6.x series. The build script publishes the adapter and packages it.

```powershell
.\scripts\Build-Installer.ps1 -Configuration Release
```

Setup is written to `Logibooks.Alta.Setup\Release\Logibooks.Alta.Adapter.Setup.exe`.
The `win-x64` profile publishes `Logibooks.Alta.Adapter.exe` under `bin\Release\net10.0\win-x64\publish-single-file` in the service project. The executable bundles the .NET runtime, native dependencies, and Russian satellite resources.

## Production installation and configuration

Run `Logibooks.Alta.Adapter.Setup.exe` with administrator privileges. Setup installs the service under `%ProgramFiles%\Logibooks\Alta` and starts it automatically. For unattended installation, use `/VERYSILENT /NORESTART` from an elevated prompt.

If an older MSI-based service or per-user Alta installation is present, uninstall it from **Installed apps** before running Setup. To reuse settings from a per-user installation, transfer the values you need from `%LOCALAPPDATA%\Logibooks\Alta\appsettings.json` into the provisioned service configuration.

Edit `%ProgramData%\Logibooks\Alta\appsettings.json` as an administrator. Administrators and SYSTEM receive full access, and `LocalService` receives read access. Setup preserves the service configuration during upgrades and uninstall.

The service writes rolling logs under `%ProgramData%\Logibooks\Alta\Logs`. Log files rotate daily or at 10 MiB, with up to 14 files retained. Use `Logging:LogLevel` to set severity and `Logging:File:Enabled` to toggle file logging. `Logging:File:Directory` accepts an absolute path or a path relative to `appsettings.json`. A custom service log directory must grant `LocalService` modify access. Restart the adapter after changing file logging settings.

An existing 0.1.0 configuration using `ConnectionStrings.AltaDatabase` must be updated to the `Databases` format before enabling probes.

```json
{
  "Service": {
    "Enabled": false,
    "MonitoringCron": "0 0 8-20/2 * * ?",
    "MonitoringTimeZone": "Europe/Moscow"
  },
  "Databases": {
    "MaxConcurrentProbes": 4,
    "Targets": [
      {
        "Name": "PrimaryAccounting",
        "Enabled": true,
        "ConnectionString": "Server=tcp:sql.example;Database=Accounting;User ID=alta;Password=replace-me;Encrypt=True;TrustServerCertificate=False"
      },
      {
        "Name": "Archive",
        "Enabled": false,
        "ConnectionString": "Server=tcp:archive.example;Database=Archive;User ID=alta;Password=replace-me;Encrypt=True;TrustServerCertificate=False"
      }
    ]
  },
  "Core": {
    "Enabled": true,
    "BaseUrl": "https://core.example/",
    "Email": "alta@example.com",
    "Password": "replace-me",
    "RequestTimeoutSeconds": 30,
    "JwtRefreshSkewMinutes": 5
  }
}
```

Target names must be nonempty and unique among enabled targets. Enabled targets need valid SQL connection strings. `Databases.MaxConcurrentProbes` accepts 1–32 and defaults to 4. When `Service.Enabled` is true, at least one database target must be enabled. Core URL and credentials are required only when both `Service.Enabled` and `Core.Enabled` are true. Core defaults to enabled. The Core account must have the `adapter-alta` role to fetch worklists and submit parcel changes. For Core integration, set each enabled target's `Name` to match an entry in Core's `AltaMonitoring:DatabaseNames`, including case. Core returns an empty worklist when the name has no match.

Set `Service.Enabled` to true only after configuration is valid. While disabled, the adapter waits for configuration changes. Windows service events appear in **Event Viewer → Windows Logs → Application** with source `Logibooks Alta Adapter`.

When both `Service.Enabled` and `Core.Enabled` are true, Alta sends heartbeats on the interval advertised by Core at login. Heartbeats run independently of the SQL and Quartz polling cycle. Core reads the interval and missed-heartbeat limit from `AltaMonitoring:Heartbeat` in its `appsettings.json`. The defaults are 600 seconds and three misses, so liveness expires 30 minutes after the last accepted heartbeat. Deploy Core first. After changing the Core interval and restarting Core, restart Alta to negotiate it immediately.

The full configuration file can be overridden with `LOGIBOOKS_ALTA_CONFIG_PATH`. Without the override, the default is ProgramData on Windows and `/etc/logibooks/alta/appsettings.json` on Linux.

## Development Compose environment

SQL Server `2025-CU7-ubuntu-24.04` listens on `127.0.0.1:1433` and includes `sqlcmd`. It uses the restored `reestr_1_1.mdf` and `reestr_1_1_log.ldf` from `D:\Projects\logibooks\AltaDB\reestr_1_1`, mounted at `/database`. The external Docker volume `alta-reestr-1-1-sql-state` holds the SQL Server catalog that already references these files. Compose startup uses this existing database, and `docker compose down` preserves the files and catalog.

Start Docker Desktop in Linux container mode. If `.env` is absent, copy `.env.example` to `.env`. Set `MSSQL_SA_PASSWORD` to the existing `sa` password for the state volume and `ALTA_DB_PASSWORD` to the password for the read-only `logibooks` login. Use single quotes around dotenv passwords containing `$`. Run the helper from the solution directory.

```powershell
.\scripts\Start-Development.ps1
```

The helper waits for the restored database to accept connections and configures the Windows adapter to connect to `reestr_1` as `logibooks`. A fresh development configuration uses `http://localhost` for Core and starts with the Core probe disabled. Run the helper again after changing the SQL port or the adapter's SQL password.

Compose pulls the prebuilt `adminer:5.5.0-standalone` image with MS SQL support. Open [Adminer](http://127.0.0.1:8083), select **MS SQL**, and sign in with server `mssql`, user `sa`, the password from `.env`, and database `reestr_1`. For an interactive SQL session, run:

```powershell
docker compose exec mssql sqlcmd -S localhost -U sa -d reestr_1 -C
```

### Full Compose stack

Run the full stack from the Alta repository with Docker Desktop in Linux container mode.

```powershell
docker compose up -d
```

- The `db` service mounts the existing PostgreSQL 17 cluster from `../.pgdata`. Set `LOGIBOOKS_CORE_PGDATA` in `.env` if your data directory is elsewhere. Run one stack at a time against that directory. If the standalone Core stack currently owns it, stop that stack before starting Alta's full stack.
- Keep the `core` and `ui` checkouts next to Alta, or set `LOGIBOOKS_CORE_DIR` and `LOGIBOOKS_UI_DIR` in `.env`.
- Sign in to the [local UI](http://localhost:18082) with your existing Logibooks account. Core connects to the existing `logibooks` database and applies its normal startup migrations. [Core Swagger](http://localhost:18080/swagger) is available on the host.
- To use Core worklists, set `Core__Enabled=true`, `Core__Email`, and `Core__Password` in `.env` for an existing account with the `adapter-alta` role. The worklist uses registers with a CMR number, `reestr_1` as their Alta database, and a status with Alta monitoring enabled.
- Alta uses the `reestr_1` SQL password from `.env`. If the password contains a double quote, set `ALTA_SQL_CONNECTION_STRING` to the complete SQL connection string with the password escaped as required by SqlClient.

Open [Adminer](http://localhost:8083) and choose the connection for the database you want to inspect.

| System | Server | Database | Username | Password setting |
| --- | --- | --- | --- | --- |
| PostgreSQL | `db` | `logibooks` | `postgres` | Existing Core development PostgreSQL password |
| MS SQL | `mssql` | `reestr_1` | `logibooks` | `ALTA_DB_PASSWORD` in `.env` |

Alta starts monitoring by default. To pause monitoring, set `ALTA_TEST_MONITORING=false` in `.env` and run `docker compose up -d` again. Follow the adapter's output with `docker compose logs -f alta`. To stop the stack and retain both databases, run `docker compose down`.

### Visual Studio Docker Compose project

Open `Logibooks.Alta.sln` with Visual Studio Container Tools and the .NET 10 SDK. Set `docker-compose` as the startup project, select **Logibooks (debug Alta)**, and press F5. The profile starts the full Compose stack and runs Alta with monitoring enabled under the debugger. The **Docker Compose** profile starts SQL Server and Adminer. **Docker Compose (Adminer)** also opens Adminer in the browser.

After preparing the local configuration with the helper, start `Logibooks.Alta.Service` with the **Alta (Windows development)** profile to debug the Windows process. From PowerShell, use:

```powershell
dotnet run --project .\Logibooks.Alta.Service --launch-profile 'Alta (Windows development)'
```

The profile sets `DOTNET_ENVIRONMENT=Development`. To probe a local Core server, enable `Core` in `Logibooks.Alta.Service/appsettings.Development.local.json` and set `Core.BaseUrl` to `http://localhost` with the port Core listens on. Staging and production require HTTPS.

## Service commands

The Windows publish supports these setup commands.

```powershell
Logibooks.Alta.Adapter.exe /Version
Logibooks.Alta.Adapter.exe /Install
Logibooks.Alta.Adapter.exe /Rollback
Logibooks.Alta.Adapter.exe /Uninstall
```

Install, rollback, and uninstall commands return a clear unsupported-platform error on Linux. Uninstall removes service registration but preserves ProgramData configuration.

## Versioning and releases

Push a tag in the form `vX.Y.Z` to publish the Windows setup EXE and its SHA-256 checksum. The release workflow sets `<Version>` in its build checkout from the tag before running tests and building the adapter and installer.

After the build succeeds, the workflow commits the new version to the default branch if the tag version is newer.

Local builds and regular CI use the version checked in to `Directory.Build.props`. Pull requests and `main` run the Windows and Linux test suites, build the setup EXE, and validate the development Compose stack.
