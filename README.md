# Logibooks Alta Adapter

<img src="Logibooks.Alta.Service/Assets/Logibooks.Alta.Adapter.png" alt="Logibooks Alta Adapter" width="160">

Logibooks Alta Adapter monitors one or more Microsoft SQL Server databases and communicates with Logibooks Core over authenticated HTTPS. Version `0.2.0` adds independently configured database targets and a Linux container environment for development. It still performs connectivity probes only; business queries, payloads, ingestion, checkpoints, and idempotency are outside this version.

## Runtime behavior

- Production runs as the self-contained x64 `LogibooksAltaAdapter` Windows service (`Logibooks Alta Adapter`) under `LocalService`.
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

Visual Studio 2022 or 2026 with **Microsoft Visual Studio Installer Projects 3.x** is required to build the MSI:

```powershell
.\scripts\Build-Installer.ps1 -Configuration Release
.\scripts\Test-Installer.ps1
```

The MSI is written to `Logibooks.Alta.Setup\Release\Logibooks.Alta.Adapter.Setup.msi`.

## Production installation and configuration

Run the per-machine MSI as an administrator. It installs the service under `%ProgramFiles%\Logibooks\Alta` and creates `%ProgramData%\Logibooks\Alta\appsettings.json` only when that file is absent. Administrators and SYSTEM receive full access; `LocalService` receives read access. Upgrades and uninstall preserve the configuration.

Version 0.2.0 deliberately does not read or migrate `ConnectionStrings.AltaDatabase`. An existing 0.1.0 configuration is preserved by the MSI but must be manually replaced with the new `Databases` contract before enabling the service:

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

Set `Service.Enabled` to true only after configuration is valid. Operational events appear in **Event Viewer → Windows Logs → Application** with source `Logibooks Alta Adapter`.

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

The Windows publish supports the MSI custom actions:

```powershell
Logibooks.Alta.Adapter.exe /Version
Logibooks.Alta.Adapter.exe /Install
Logibooks.Alta.Adapter.exe /Rollback
Logibooks.Alta.Adapter.exe /Uninstall
```

Install, rollback, and uninstall commands return a clear unsupported-platform error on Linux. Uninstall removes service registration but preserves ProgramData configuration.

## Versioning and releases

Set the version for a release with one command:

```powershell
.\scripts\Set-Version.ps1 0.3.0
```

`Directory.Build.props` supplies the version for local builds, Windows publishes, and the adapter in Docker. The command sets that version and synchronizes the Visual Studio installer project. When the MSI version changes, it regenerates `ProductCode` and `PackageCode` and preserves the stable `UpgradeCode`.

Compose ignores `ALTA_VERSION` entries in existing `.env` files. Use `docker compose run --rm --no-deps adapter --version` to read the version built into the development image.

Release tags must match the project version (`vX.Y.Z`). Pull requests and `main` run Windows tests and MSI validation alongside Linux tests and development image builds. Version tags publish the MSI and its SHA-256 checksum.
