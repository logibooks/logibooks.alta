# Logibooks Alta Adapter

<img src="Logibooks.Alta.Service/Assets/Logibooks.Alta.Adapter.png" alt="Logibooks Alta Adapter" width="160">

Logibooks Alta Adapter monitors one or more independently configured Microsoft SQL Server databases and communicates with Logibooks Core over authenticated HTTPS. The adapter performs connectivity probes and can run in a Linux container for development.

## Runtime behavior

- The self-contained x64 adapter runs as the `LogibooksAltaAdapter` Windows service under `LocalService` or as a console app under the current Windows account.
- Production SQL Server instances and databases remain external and are not installed or managed by the adapter.
- Development can run the adapter in Linux alongside SQL Server 2025 and MSSQL-enabled Adminer through Docker Compose.
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

- **Console app** installs for your Windows account without elevation. Use **Edit console configuration** in the Start menu to open `%LOCALAPPDATA%\Logibooks\Alta\appsettings.json`, then launch **Logibooks Alta Adapter (Console)**. The adapter logs to the console and runs until you close it.
- **Windows service** requests administrator credentials, installs under `%ProgramFiles%\Logibooks\Alta`, and starts automatically under `LocalService`. Edit `%ProgramData%\Logibooks\Alta\appsettings.json` as an administrator. Administrators and SYSTEM receive full access, and `LocalService` receives read access. If an older MSI-based service is installed, uninstall it from **Installed apps** before choosing this mode.

Both modes preserve their configuration during upgrades and uninstall. For unattended installation, use `/CURRENTUSER /VERYSILENT /NORESTART` for console mode or `/ALLUSERS /VERYSILENT /NORESTART` from an elevated prompt for service mode.

Windows installations write rolling logs under `%ProgramData%\Logibooks\Alta\Logs` for the service and `%LOCALAPPDATA%\Logibooks\Alta\Logs` for the console app. Log files rotate daily or at 10 MiB, with up to 14 files retained. Use `Logging:LogLevel` to set severity and `Logging:File:Enabled` to toggle file logging. `Logging:File:Directory` accepts an absolute path or a path relative to `appsettings.json`. A custom service log directory must grant `LocalService` modify access. Restart the adapter after changing file logging settings. Linux container deployments use console logging by default.

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

The default development stack contains:

- SQL Server `2025-CU7-ubuntu-24.04`, bound to `127.0.0.1:1433`.
- An idempotent restore job for `D:\Projects\logibooks\AltaDB\gtk_2026-07-30.bak` only.
- The Linux adapter monitoring `gtk` as `AltaInstance3`, with Core disabled.
- Adminer 5.5.0 with its FreeTDS `pdo_dblib` MSSQL driver, at [http://localhost:8083](http://localhost:8083).

The 142 GB `reestr_1_1.bak` backup is intentionally excluded from automatic startup. SQL system databases plus `gtk.mdf` and `gtk_log.ldf` persist under `D:\Projects\logibooks\AltaInstance3`.

Start Docker Desktop, then run:

```powershell
.\scripts\Start-Development.ps1
docker compose logs --follow adapter
```

The helper checks Docker, verifies the backup, creates the data directory, creates an ignored `.env` with policy-compliant generated passwords when absent, and starts the stack. Paths and loopback ports can be changed in `.env`; `.env.example` documents the contract. The adapter connects through the dedicated `alta_adapter` login, which has `CONNECT` and `db_datareader` only.

### Visual Studio Docker Compose project

The solution includes a `Microsoft.Docker.Sdk` Docker Compose project equivalent to the sibling Core solution. Visual Studio Container Tools are required. The project can be loaded before `.env` exists; run `Start-Development.ps1` before the first Compose launch to create the ignored `.env` and required host directory, then select one of these startup profiles in Visual Studio:

- **Docker Compose** starts the complete dependency-aware stack and attaches the debugger to `adapter`.
- **Docker Compose (Adminer)** starts the complete stack without attaching a debugger and opens Adminer in the browser.

The project uses regular container build mode because the adapter Dockerfile is also the reproducible CLI and CI development image. Visual Studio honors SQL health and restore completion through `DependencyAwareStart` before starting the adapter.

Normal `docker compose down` / `up` preserves the database and skips restoration when `gtk` already exists. For one clean restore, use the guarded reset helper and type its exact confirmation phrase:

```powershell
.\scripts\Reset-Development.ps1
.\scripts\Start-Development.ps1
```

The reset helper rejects empty, root, repository, backup, and unexpected target paths and deletes only the resolved `AltaInstance3` directory contents.

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

Update `<Version>` in `Logibooks.Alta.Service/Logibooks.Alta.Service.csproj` for each release. Local builds, Windows publishes, the Windows setup EXE, and the adapter in Docker all use this version.

Use `docker compose run --rm --no-deps adapter --version` to read the version built into the development image.

Release tags must match the project version (`vX.Y.Z`). Pull requests and `main` run Windows tests and build the setup EXE alongside Linux tests and development image builds. Version tags publish the setup EXE and its SHA-256 checksum.
