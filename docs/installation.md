# Installation guide

Two parts: the **central server** (your PC) and the **agent** (every employee PC).

## 1. Central server

### Prerequisites (central PC)

* Windows 10/11 or Server 2019+, .NET SDK 10, Node.js 20+ (24 tested), PostgreSQL 14+ (18 tested).
* A PostgreSQL role and database for NISS, e.g.
  ```sql
  CREATE ROLE niss LOGIN PASSWORD 'choose-a-strong-password';
  CREATE DATABASE niss_monitor OWNER niss;
  ```
  (or let the launcher create the database: `-CreateDatabase`).

### One command

```powershell
cd <repo>
.\NISS.Server.Start.ps1 -DbPassword 'choose-a-strong-password' -CreateDatabase
```

It (1) checks .NET / Node, (2) checks PostgreSQL connectivity and the database, (3) creates a self-signed LAN certificate if none is
configured, (4) builds the API and applies migrations (`NISS.Api.dll --migrate`), (5) starts the API, (6) builds and starts the
dashboard, (7) prints the LAN URLs, (8) health-checks both. Stop with `.\NISS.Server.Stop.ps1`.

First start prints the bootstrap administrator (`admin` + a random password, shown once). Change it under Settings -> Admin users.

### Configuration (`server\NISS.Api\serversettings.json`)

```json
{
  "Server":   { "BindIp": "0.0.0.0", "Port": 8443, "Certificate": { "Path": "", "Password": "", "Thumbprint": "" }, "AllowInsecureHttp": false },
  "Dashboard":{ "Port": 3000 },
  "Database": { "ConnectionString": "Host=localhost;Port=5432;Database=niss_monitor;Username=niss;Password=...", "ApplyMigrationsOnStartup": true },
  "Security": { "JwtSigningKey": "", "AccessTokenMinutes": 15, "RefreshTokenHours": 8, "BootstrapAdminUsername": "admin", "BootstrapAdminPassword": "" },
  "Monitoring": { "HeartbeatSeconds": 30, "MetricsSeconds": 30, "IdleThresholdMinutes": 5, "HeartbeatWarningSeconds": 60, "OfflineSeconds": 300, "BacklogWarningThreshold": 5000, "MinAgentVersion": "" },
  "Retention": { "RawEventsDays": 90, "ApplicationSummariesDays": 365, "DeviceHealthDays": 180, "AuditLogsDays": 365 }
}
```

Put secrets (database password, certificate password) in **`serversettings.local.json`** (git-ignored, same shape) or environment
variables `NISS_Database__ConnectionString`, `NISS_Server__Certificate__Password`, ... The launcher and certificate script do this for you.

### Database

Migrations are applied automatically at startup and by `NISS.Api.dll --migrate`. See [database.md](database.md) for the schema,
raw SQL (`deployment\database\schema.sql`) and manual migration commands.

### Manual start (without the launcher)

```powershell
dotnet run --project server\NISS.Api -c Release                 # API
cd web\niss-monitor-dashboard; npm ci; npm run build; npm run start -- -H 0.0.0.0 -p 3000
```

### Run as Windows services (optional, for a permanent central PC)

Use NSSM or `New-Service` around `dotnet NISS.Api.dll` and `npm run start`, or publish the API
(`dotnet publish server\NISS.Api -c Release -o C:\NISS\api`) and register `NISS.Api.exe` as a service. Keep `serversettings.local.json` next to it.

## 2. Agent (each employee PC)

### Build the installer (once)

```powershell
.\deployment\scripts\Build-Agent.ps1        # runs the agent tests, publishes, packages
# -> artifacts\agent\NISSMonitorSetup.exe  (single file, payload embedded, ~73 MB, self-contained: no .NET needed on PCs)
```

### Create an enrollment token

Dashboard -> Devices -> **Enrollment tokens** -> *New token* (optionally bound to an employee code such as `EMP1024`; expires in 24 h by default).
The token is shown once. It is single-use.

### Install (elevated prompt on the employee PC)

```powershell
NISSMonitorSetup.exe --server https://192.168.1.10:8443 --token <one-time-token> --thumbprint <sha256-pin> [--employee EMP1024] [/silent]
```

The installer performs, with visible progress: verify Windows -> stop old services -> install files (`C:\Program Files\NISS\Monitor`) ->
create `C:\ProgramData\NISS\Agent` (SYSTEM + Administrators only) -> write `agentsettings.json` -> install the Windows service (automatic start,
LocalSystem, recovery = restart / restart / restart) -> install the watchdog service -> register session-agent startup -> create the SQLite
database -> **enroll** -> start both services -> validate communication -> report the result. Log: `C:\ProgramData\NISS\Agent\Logs\installer-YYYYMMDD.log`.

Two services are installed as a mutual watchdog pair: `NISSMonitorService` (the agent) and `Watchdog` (a small companion that
restarts it if anyone stops it via Task Manager, Services.msc, or `sc stop` - and vice versa). Both are ordinary, fully visible services;
`Watchdog`'s Properties/Description in `services.msc` names it as belonging to NISS Monitor. See
[security.md](security.md#mutual-watchdog-resilience-against-a-casual-stop) for exactly what this does and does not protect against.

PowerShell alternative (no exe needed): `deployment\install\Install.ps1 -Payload NISSMonitorPayload.zip -Server ... -Token ... -Thumbprint ...`.

### Management

| Task | Command |
|---|---|
| Status / health | `NISSAgent.exe --status`, `NISSAgent.exe --health` |
| Device id | `NISSAgent.exe --device-id` |
| Diagnostics bundle | `NISSAgent.exe --diagnostics [path]` |
| Re-enroll (new token) | `NISSAgent.exe --enroll --server <url> --token <token>` |
| Rotate credential | `NISSAgent.exe --rotate-credentials` |
| Repair | `NISSMonitorSetup.exe --repair` or `deployment\install\Repair.ps1` or `NISSAgent.exe --repair` |
| Uninstall | `NISSMonitorSetup.exe --uninstall [--purge]` or `deployment\uninstall\Uninstall.ps1 [-Purge]` |

All `NISSAgent` commands except `--version` require an elevated prompt.

### Mass deployment

Wrap the silent command in your tooling (Intune Win32 app, GPO startup script, SCCM/RMM), generating one token per device
(`POST /api/v1/enrollment-tokens`) or reusing the same install command with per-device tokens.

## 3. Verifying an installation

1. `Get-Service NISSMonitorService` -> Running, StartType Automatic.
2. `NISSAgent.exe --status` -> enrolled, queue depth ~0, recent heartbeat.
3. Dashboard -> Live Monitoring: the device appears within ~30 s.
4. Log in as a user: a session process `NISSMonitor.Session.exe` runs in their session; the card shows their current application.
