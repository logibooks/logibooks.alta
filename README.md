# Logibooks Alta Adapter

<img src="Logibooks.Alta.Service/Assets/Logibooks.Alta.Adapter.png" alt="Logibooks Alta Adapter" width="30">

Logibooks Alta Adapter is a Windows application that monitors one or more Microsoft SQL Server databases and communicates with Logibooks Core over authenticated HTTPS.

## Runtime behavior

- The self-contained x64 adapter runs as the `LogibooksAltaAdapter` Windows service under `LocalService` or as a console app under the current Windows account.
- Production SQL Server instances and databases remain external and are not installed or managed by the adapter.
- In development, run the adapter on Windows with SQL Server 2025 and Adminer in Docker Desktop's Linux containers.
- Each enabled target executes `SELECT CAST(1 AS int)`. Database probes run independently with bounded concurrency.
- The optional Core probe runs concurrently with the database probes. It signs in through `POST /api/Auth/login`, requires the `adapter-alta` role, caches the JWT in memory, and calls `GET /api/Auth/check`.
- Transient network and HTTP 5xx failures receive up to three attempts with 1- and 2-second delays. One fresh-login retry is made after HTTP `401`.
- Configuration reloads without restarting the process. A change received during an active probe cycle causes the next cycle to start immediately.
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

Open `Logibooks.Alta.Adapter.Setup.exe` and choose the installation mode.

- **Console app** installs for your Windows account without elevation. In the Start menu's **Logibooks** folder, use **Open Alta adapter configuration** to open `%LOCALAPPDATA%\Logibooks\Alta\appsettings.json`, then select **Start Alta adapter in console mode**. The adapter logs to the console and runs until you close it.
- **Windows service** requests administrator credentials, installs under `%ProgramFiles%\Logibooks\Alta`, and starts automatically under `LocalService`. Edit `%ProgramData%\Logibooks\Alta\appsettings.json` as an administrator. Administrators and SYSTEM receive full access, and `LocalService` receives read access. If an older MSI-based service is installed, uninstall it from **Installed apps** before choosing this mode.

Both modes preserve their configuration during upgrades and uninstall. For unattended installation, use `/CURRENTUSER /VERYSILENT /NORESTART` for console mode or `/ALLUSERS /VERYSILENT /NORESTART` from an elevated prompt for service mode.

Windows installations write rolling logs under `%ProgramData%\Logibooks\Alta\Logs` for the service and `%LOCALAPPDATA%\Logibooks\Alta\Logs` for the console app. Log files rotate daily or at 10 MiB, with up to 14 files retained. Use `Logging:LogLevel` to set severity and `Logging:File:Enabled` to toggle file logging. `Logging:File:Directory` accepts an absolute path or a path relative to `appsettings.json`. A custom service log directory must grant `LocalService` modify access. Restart the adapter after changing file logging settings.

An existing 0.1.0 configuration using `ConnectionStrings.AltaDatabase` must be updated to the `Databases` format before enabling probes.

```json
{
  "Service": {
    "Enabled": false,
    "PollIntervalSeconds": 60
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

Target names must be nonempty and unique among enabled targets. Enabled targets need valid SQL connection strings. `Databases.MaxConcurrentProbes` accepts 1–32 and defaults to 4. When `Service.Enabled` is true, at least one database target must be enabled. Core URL and credentials are required only when both `Service.Enabled` and `Core.Enabled` are true; Core defaults to enabled.

Set `Service.Enabled` to true only after configuration is valid. In service mode, operational events appear in **Event Viewer → Windows Logs → Application** with source `Logibooks Alta Adapter`.

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

### Visual Studio Docker Compose project

The `docker-compose.dcproj` project is included in `Logibooks.Alta.sln` and uses Visual Studio Container Tools. Its **Docker Compose** profile starts SQL Server and Adminer. **Docker Compose (Adminer)** also opens Adminer in the browser.

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

Update `<Version>` in `Logibooks.Alta.Service/Logibooks.Alta.Service.csproj` for each release. Local builds, Windows publishes, and the Windows setup EXE use this version.

Release tags must match the project version (`vX.Y.Z`). Pull requests and `main` run the Windows and Linux test suites, build the setup EXE, and validate the development Compose stack. Version tags publish the setup EXE and its SHA-256 checksum.
