# NISS Employee PC Monitoring System

Authorized, transparent telemetry for **organization-owned / managed Windows PCs**, running entirely inside your LAN.

* **Endpoint agent** - a normal Windows Service (`NISSMonitor.Service.exe`) plus a per-session process (`NISSMonitor.Session.exe`) and an admin CLI (`NISSAgent.exe`).
* **Central server** - ASP.NET Core API + SignalR + PostgreSQL, running on your PC.
* **Dashboard** - Next.js / React / Tailwind, live via SignalR.
* No cloud services. No browser history, no key logging. **Optional** screenshots + live screen view, SUPERADMIN-only and **off by default** (see [Screen capture & live view](#screen-capture--live-view)).

> This system is for *authorized* monitoring of company-managed devices. Tell employees what is collected and follow your local legal
> requirements. It deliberately contains no hidden persistence, rootkit, credential-capture or anti-admin behavior. See [docs/security.md](docs/security.md).

## What it reports

Process start/stop - application (window-owner) open/close - foreground application sessions with duration - **running vs foreground vs interaction vs idle**
(kept separate everywhere: data model, API, dashboard, reports) - idle/active/locked - login/logout/connect/disconnect - mouse and keyboard activity **counts only** -
CPU/RAM/disk/network/battery/displays - USB arrival/removal - optional file metadata events (off by default) - agent health, backlog, errors, crashes.

## Quick start

```powershell
# 1. central PC: start everything (checks deps + PostgreSQL, migrates, starts API + dashboard, prints LAN URLs)
.\NISS.Server.Start.ps1 -DbPassword '<postgres password>' -CreateDatabase

# 2. build the agent installer (runs the agent tests first)
.\deployment\scripts\Build-Agent.ps1            # -> artifacts\agent\NISSMonitorSetup.exe

# 3. dashboard -> Devices -> Enrollment tokens -> New token, then on each employee PC (elevated):
NISSMonitorSetup.exe --server https://192.168.1.10:8443 --token <token> --thumbprint <sha256 pin>
```

Open `http://192.168.1.10:3000` and sign in with the bootstrap administrator printed by the launcher (change the password).

## Repository

```
src/            NISS.Monitor.{Protocol,Core,Infrastructure,Service,Session,Cli,Installer,Tests}
server/         NISS.{Domain,Application,Infrastructure,Workers,Api,Api.Tests}
web/            niss-monitor-dashboard (Next.js)
deployment/     install/ uninstall/ database/ certificates/ scripts/
docs/           architecture, api, database, installation, troubleshooting, security, network, monitoring-events, developer, testing, upgrade
NISS.Server.Start.ps1 / NISS.Server.Stop.ps1
```

## Documentation

| | |
|---|---|
| [Architecture](docs/architecture.md) | components, data flow, offline-first design, time handling, privacy by construction |
| [Installation](docs/installation.md) | server + agent install, configuration, management commands |
| [Network](docs/network.md) | ports, firewall, certificates and pinning, LAN addressing |
| [Security](docs/security.md) | threat model, hardening checklist, preventing unauthorized removal |
| [API](docs/api.md) | REST + SignalR reference |
| [Monitoring events](docs/monitoring-events.md) | event catalogue, payloads, projections |
| [Database](docs/database.md) | schema, migrations, retention |
| [Troubleshooting](docs/troubleshooting.md) | symptoms, causes, commands |
| [Developer](docs/developer.md) / [Testing](docs/testing.md) / [Upgrade](docs/upgrade.md) | |

## Build, test, run (developers)

```powershell
dotnet build NISS-PC-Monitor.slnx
dotnet test  src\NISS.Monitor.Tests
dotnet test  server\NISS.Api.Tests
cd web\niss-monitor-dashboard ; npm ci ; npm test ; npm run build
.\deployment\scripts\Test-EndToEnd.ps1        # real agent -> real API -> real PostgreSQL
```

Requirements: Windows 10 1809+/11, .NET SDK 10, Node.js 20+, PostgreSQL 14+.

## Implemented monitoring capabilities

Process start/stop with parent/path/user/session, optional command line + SHA-256 (policy) - window-owner application open/close - foreground application **sessions** with foreground / interaction / idle seconds -
idle / active / locked, login / logout, session connect / disconnect - keyboard and mouse **activity counts** (clicks, double-clicks, wheel, optional cursor sampling; no characters) -
CPU / RAM / disk / uptime / OS / manufacturer / model / serial / IPs / MACs / battery / displays - network up/down - USB arrival/removal - optional file metadata (create/modify/rename/delete) -
heartbeat, clock-offset correction, offline queue with ordered retry and idempotent upload, credential rotation, disable / revoke / re-enroll, agent health (offline, delayed, stopped, session agent missing, backlog, outdated, auth failure, crash recovery),
audit log, reports (8 kinds + CSV), application categories, retention worker, live dashboard over SignalR -
**optional** policy-gated **screenshots** (silent periodic stills → per-device timeline, retention-pruned) and **live screen view** (on-demand, view-only, employee sees a notice), both SUPERADMIN-only and off by default.

## Screen capture & live view

Optional, **SUPERADMIN-only**, and **disabled by default** (enable per scope in Settings → policy, pilot one PC first):

* **Silent periodic screenshots** — the session agent captures the screen on an interval (default 60s, only when unlocked and active), uploads JPEGs stored as files on the server with a metadata row, viewable as a per-device **Screens → timeline** in the dashboard. Auto-deleted after `ScreenshotStorage:RetentionDays` (default 14). **No employee-visible indication.**
* **Live view ("like UltraViewer", view-only)** — a SUPERADMIN watches a device's screen in near real time over a persistent agent websocket relayed through SignalR. No input/control, **nothing recorded**. While a live session is active the employee sees a small, click-through on-screen notice that their screen is being viewed; capping at `ScreenLive:MaxSessionMinutes` (default 30).
* Every stored-image view and every live session is written to the **audit log**.

Screenshots can contain anything on screen (credentials, personal, financial, health data). They are stored as **plaintext JPEGs** with ordinary file permissions — lock down the server and the `ScreenshotStorage:Root` folder. Silent monitoring of company devices is normally lawful **only where the organization has disclosed in writing (employment contract / acceptable-use policy) that company devices are monitored**; this system assumes that disclosure exists and does not create it. This is not legal advice.

## Intentionally excluded (by design)

Raw keylogging / typed characters, passwords, OTPs, clipboard harvesting, webcam/microphone, browser history / URLs / DNS reconstruction / HTTPS inspection
(`IBrowserMonitoringProvider` reports "Not Enabled"; V2), rootkit / kernel / stealth persistence / code injection, disabling security software, anti-admin protection, hidden binaries, unauthorized remote execution.

## What was verified (and what was not)

| Verified in development | How |
|---|---|
| Agent logic + Windows integration | 57 automated tests (engines, SQLite queue, offline / reconnect / duplicates, heartbeat + clock offset, enrollment + DPAPI, collector supervision, unclean-shutdown detection, named-pipe IPC with session-process restart, real Win32 process / foreground / idle / metrics / WTS / USB smoke tests) |
| Server | 153 automated tests against real PostgreSQL (enrollment, auth, validation, idempotent ingestion, projections, health worker, SignalR, reports, audit, rate limits, retention) |
| Dashboard | 75 unit tests; production build; all 17 pages driven in Chrome against the real API with 0 console errors / 0 failed API calls |
| Whole pipeline | `Test-EndToEnd.ps1`: **24/24** - real agent + session process -> real API -> real PostgreSQL -> SignalR: enrollment, employee mapping, heartbeat, process start/stop, foreground sessions, input counts (8 injected key taps = exactly 8 counted, no characters stored), metrics, outage buffering + ordered reconnect upload, no duplicates, graceful stop -> SERVICE_STOPPED, restart, hard-kill detection, crash recovery |
| Server launcher + TLS | `NISS.Server.Start.ps1` ran for real: DB creation, self-signed LAN certificate, migrations, HTTPS API, dashboard, health checks; agent certificate **pinning** verified (correct pin OK, wrong pin rejected, unpinned self-signed rejected) |
| Installer | built, packaged (73 MB single file, payload embedded), CLI exercised (`--version/--init/--status/--health/--device-id`, non-elevated refusal) |

**Not verified in this development session (needs an elevated, real PC):** installing the actual Windows service with `NISSMonitorSetup.exe` / `Install.ps1` (service registration, recovery actions, Event Log source, HKLM Run key, the
service launching `NISSMonitor.Session.exe` via `CreateProcessAsUser` as LocalSystem), start-after-reboot, USB plug/unplug on real hardware, lock/unlock, RDP sessions, the 5-minute idle scenario, and the manual 28-step scenario in
[docs/testing.md](docs/testing.md). The installer/PowerShell scripts were syntax-checked and their shared logic is exercised by the CLI, but the elevated install path itself has **not been run**. Do a pilot on one PC first.

## Known limitations

* Process start/stop is a 2-second snapshot diff (not ETW); processes shorter than ~2 s can be missed.
* The binaries are not code-signed; sign them (and consider AppLocker/WDAC) before wide rollout. A local administrator can stop or remove the agent by design; a standard user can end their own session process (it is relaunched within ~15 s and the server shows "session agent missing" meanwhile).
* Long report ranges are aggregated in memory (fine at LAN scale); the timeline endpoint is capped at 2000 events per query.
* SignalR delivers `EventIngested` twice to a client subscribed to both the feed and one device (the dashboard dedupes on `eventId`).
* Immediately after a server restart, devices can briefly show WARNING/OFFLINE until their next heartbeat.
* The 5 MB request-size limit is enforced by Kestrel only (not exercised by tests); WebSocket transport is exercised only by the E2E probe, not by the server test suite (which uses long polling).
* Reassigning a device to another employee does not back-fill `employee_id` on old rows.
* The API is not packaged as a Windows service (run it via the launcher or wrap it with NSSM); certificate-store thumbprint loading is untested (PFX is tested).
* Display information is approximate when the service (session 0) cannot query the desktop (falls back to video controller resolution).

## Security considerations

See [docs/security.md](docs/security.md): per-device credentials (SHA-256 at rest, DPAPI on the PC), pinned TLS, one-time enrollment tokens, RBAC, audit log, rate limiting, input validation, log redaction, session-pipe verification,
and how to prevent unauthorized removal with standard Windows policy. Change the bootstrap admin password, keep `serversettings.local.json` private, restrict the firewall to your LAN, and use a dedicated PostgreSQL role.
