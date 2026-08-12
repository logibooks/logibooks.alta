# Logibooks Alta Adapter

<img src="Logibooks.Alta.Service/Assets/Logibooks.Alta.Adapter.png" alt="Logibooks Alta Adapter" width="160">

Logibooks Alta Adapter is a Windows service that monitors a Microsoft SQL Server database and communicates with Logibooks Core over authenticated HTTPS. Version `0.1.0` is the connectivity foundation: it probes both systems but does not read or transmit business data.

## What version 0.1.0 does

- Runs as the `LogibooksAltaAdapter` Windows service (`Logibooks Alta Adapter`) under `LocalService`.
- Starts automatically with delayed startup and remains safely idle until enabled.
- Reloads its ProgramData configuration without restarting the service.
- Executes `SELECT CAST(1 AS int)` against MSSQL.
- Signs in to Core through `POST /api/Auth/login`, requires the `adapter-alta` role, caches the JWT in memory, and calls `GET /api/Auth/check`.
- Refreshes a JWT before expiry and performs one fresh-login retry after an HTTP `401`.
- Retries transient network and HTTP 5xx failures three times, with 1- and 2-second delays.
- Logs version, state changes, successful probes, and sanitized errors to the Windows Application event log.

No business query, payload, ingestion endpoint, checkpoint, or idempotency behavior is part of this version.

## Prerequisites

- Windows x64.
- .NET 10 SDK for development. The installed service is self-contained and does not require a separately installed runtime.
- Visual Studio 2022 or 2026 with **Microsoft Visual Studio Installer Projects 3.x** to build the MSI.

## Build and test

From PowerShell:

```powershell
dotnet restore .\Logibooks.Alta.sln
dotnet test .\Logibooks.Alta.Tests\Logibooks.Alta.Tests.csproj --configuration Release
dotnet publish .\Logibooks.Alta.Service\Logibooks.Alta.Service.csproj `
  --configuration Release --runtime win-x64 --self-contained true `
  -p:PublishProfile=win-x64
```

Build the Visual Studio installer project and its self-contained publish output with:

```powershell
.\scripts\Build-Installer.ps1 -Configuration Release
.\scripts\Test-Installer.ps1
```

The MSI is written to `Logibooks.Alta.Setup\Release\Logibooks.Alta.Adapter.Setup.msi`.

## Install and configure

Run the MSI as an administrator. It installs the binaries under:

```text
%ProgramFiles%\Logibooks\Alta
```

The first install creates this configuration file only when it does not already exist:

```text
%ProgramData%\Logibooks\Alta\appsettings.json
```

The installer grants full access only to Administrators and SYSTEM, and read access to `LocalService`. Upgrades and uninstall preserve the file.

Initial configuration:

```json
{
  "Service": {
    "Enabled": false,
    "PollIntervalSeconds": 60
  },
  "ConnectionStrings": {
    "AltaDatabase": "Server=tcp:sql.example;Database=Alta;User ID=alta;Password=replace-me;Encrypt=True;TrustServerCertificate=False"
  },
  "Core": {
    "BaseUrl": "https://core.example/",
    "Email": "alta@example.com",
    "Password": "replace-me",
    "RequestTimeoutSeconds": 30,
    "JwtRefreshSkewMinutes": 5
  }
}
```

Set `Service.Enabled` to `true` only after all required values are valid. The service notices a saved configuration change and starts probing immediately. `Core.BaseUrl` must be an absolute HTTPS URL; certificate validation uses the Windows trusted certificate store.

Operational events appear in **Event Viewer → Windows Logs → Application** with source `Logibooks Alta Adapter`. Passwords, connection strings, and JWTs are never intentionally logged.

## Service command-line operations

The published executable supports the commands used by the MSI:

```powershell
Logibooks.Alta.Adapter.exe /Version
Logibooks.Alta.Adapter.exe /Install
Logibooks.Alta.Adapter.exe /Rollback
Logibooks.Alta.Adapter.exe /Uninstall
```

`/Install`, `/Rollback`, and `/Uninstall` require administrator rights. Installation is idempotent. Rollback and uninstall remove only the Windows service registration; they do not remove ProgramData configuration.

## Versioning and releases

Checked-in version locations are synchronized at `0.1.0`. To prepare another release:

```powershell
.\scripts\Set-Version.ps1 0.2.0
git commit -am "Release 0.2.0"
git tag v0.2.0
git push origin main --tags
```

The version script validates MSI-compatible `X.Y.Z`, updates executable and installer metadata, creates fresh MSI `ProductCode` and `PackageCode` values, and preserves the stable `UpgradeCode`. `vX.Y.Z` tags are authoritative; release automation rejects a tag that does not match the checked-in version, then publishes the MSI and its SHA-256 checksum to the GitHub release.

## Administrative smoke test

Use a disposable Windows test host with test-only MSSQL and Core credentials:

1. Install the MSI as administrator and confirm `Logibooks Alta Adapter` is running but idle.
2. Save valid test configuration with `Service.Enabled` set to `true`.
3. Confirm successful MSSQL and Core probe events in Event Viewer.
4. Build a higher test version with `Set-Version.ps1`, install it, and confirm the existing ProgramData configuration remains unchanged.
5. Uninstall from Apps & features and confirm the service and Program Files directory are removed while ProgramData configuration remains.

Do not use production credentials for this test.
