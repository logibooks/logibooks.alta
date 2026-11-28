## Development Compose environment

SQL Server `2025-CU7-ubuntu-24.04` listens on `127.0.0.1:1433` and uses an empty `reestr_1` baseline built from the native schema. Compose restores it into the project’s `sql-dev-state` volume on first start. The backup and its manifest are mounted read-only. The initializer verifies the development marker and provisions the `logibooks` reader and `alta_dev_writer` logins before Compose starts Alta or Adminer. Regular startup preserves imported documents.

### Prepare the compact backup once

For a fresh checkout, copy the prepared `reestr_1-dev.bak` and `reestr_1-dev.bak.manifest.json` into `.local/alta/`. If you keep the pair in another location, set `ALTA_DEV_BASELINE_BAK` to its `.bak` path in `.env`. A checkout with the prepared pair can start directly with the steps below.

Start the restored source SQL Server and obtain a connection to its `reestr_1` with schema extraction access. Install [SqlPackage](https://learn.microsoft.com/sql/tools/sqlpackage/sqlpackage-download) and run the following in PowerShell 7.3 or later.

```powershell
$env:ALTA_SOURCE_CONNECTION_STRING = '<source reestr_1 connection string>'
.\scripts\New-DevelopmentBaseline.ps1 -BackupPath '.\.local\alta\reestr_1-dev.bak'
```

The script extracts schema only and applies it in an isolated scratch SQL Server on `127.0.0.1:11433`. The native lists use foreign keys to `Docs.GID` and the verified schema has no list-writing triggers. The pinned Linux image lacks the optional full-text component for the `Docs` index. The script skips that index, checks integrity and emptiness, then writes the `.bak` and adjacent `.bak.manifest.json`. To supply a reviewed schema-only SQL script when SqlPackage cannot represent a required source object, pass `-SchemaSqlPath`. Keep the two generated files together.

### Start SQL Server and Adminer

Start Docker Desktop in Linux container mode. Copy `.env.example` to `.env` if needed. Set `MSSQL_SA_PASSWORD`, `ALTA_DB_PASSWORD`, and `ALTA_DEV_WRITER_PASSWORD`. Use single quotes around dotenv passwords containing `$`.

```powershell
.\scripts\Start-Development.ps1
```

The helper waits for initialization and configures the Windows adapter to connect to `reestr_1` as `logibooks`. A fresh development configuration uses `http://localhost` for Core and starts with the Core probe disabled. Run the helper again after changing the SQL port or the adapter's SQL password.

Compose pulls the prebuilt `adminer:5.5.0-standalone` image with MS SQL support. Open [Adminer](http://127.0.0.1:8083), select **MS SQL**, and sign in with server `mssql`, user `sa`, the password from `.env`, and database `reestr_1`. For an interactive SQL session, run:

```powershell
docker compose exec mssql sqlcmd -S localhost -U sa -d reestr_1 -C
```

### Full Compose stack

Run the full stack from the Alta repository with Docker Desktop in Linux container mode.

```powershell
docker compose up -d --build
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

Alta starts monitoring every 15 seconds in Compose. Set `ALTA_TEST_MONITORING_CRON` to use a different Quartz schedule. To pause monitoring, set `ALTA_TEST_MONITORING=false` in `.env` and run `docker compose up -d` again. Follow the adapter's output with `docker compose logs -f alta`. To stop the stack and keep both databases, run `docker compose down`.

### Load a register and replay decisions

Upload a test register in Core, set a unique CMR and the `reestr_1` Alta target, and ensure the register's status enables Alta monitoring. Download the full XML archive for eligible parcels. The tool accepts that ZIP directly or one exported `AltaIndPost` XML file.

```powershell
$env:ALTA_DEV_WRITER_CONNECTION_STRING = 'Server=tcp:127.0.0.1,1433;Database=reestr_1;User ID=alta_dev_writer;Password=replace-me;Encrypt=True;TrustServerCertificate=True'
$tool = '.\Logibooks.Alta.DevTool'
dotnet run --project $tool -- import --cmr CMR-42 .\IndPost_CMR-42.zip
dotnet run --project $tool -- declare --cmr CMR-42 --dec 10000000/251126/0000042 --procedure 40 --parcel 00042
dotnet run --project $tool -- decide --cmr CMR-42 --dec 10000000/251126/0000042 --code 11
dotnet run --project $tool -- decide --cmr CMR-42 --dec 10000000/251126/0000042 --code 11 --parcel 00042 --weight-kg 2.50
dotnet run --project $tool -- decide --cmr CMR-42 --dec 10000000/251126/0000042 --code 10
```

Use the CMR, procedure, and parcel numbers from your register and choose DEC numbers unused by Core. Wait for the next adapter cycle between decisions. In **Решения о выпуске**, verify the goods, line totals, weight change, and final release from **Синхронизация**. Keep a second parcel unlinked until these checks are complete so the register stays on the worklist. Declare it with a new DEC. With no `--parcel` flags, `declare` takes all currently unlinked parcels and `decide` updates all members of the selected DEC.

The tool preserves quantities from the export, including fractions. Core applies its own validation to submitted decisions, including its whole-unit quantity rule.

For a standalone executable, run `dotnet publish .\Logibooks.Alta.DevTool -c Release --self-contained false` on Windows. Run `Logibooks.Alta.DevTool.exe` with the same arguments and writer connection setting.

To clear the development SQL database, stop any Windows adapter debugging session and run `.\scripts\Start-Development.ps1 -ResetAlta`. The helper checks the development volume and database marker. It restores the backup under a temporary name and checks its marker, schema, and emptiness before swapping it into place. The helper resumes the Compose adapter if it was running. Use a new CMR and new DEC numbers for the next replay because the existing Core database retains its decisions.

Reset uses the active SQL reader and writer passwords. If either password changes in `.env`, run normal startup first to apply the new credentials, then reset.

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