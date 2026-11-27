# Architecture

NISS Employee PC Monitoring System - authorized, transparent telemetry for organization-managed Windows PCs.
Everything runs inside the LAN; no cloud service is required.

```
Employee PC (many)                                   Central PC (one, your LAN)
+--------------------------------------------+        +---------------------------------------------+
| NISSMonitor.Service.exe  (Windows Service)  |        |  NISS.Api  (ASP.NET Core, Kestrel :8443)    |
|  - process start/stop     (toolhelp diff)   |        |    REST /api/v1  +  SignalR /hubs/monitor    |
|  - login/logout/connect   (WTS poll)        | HTTPS  |    ingestion (idempotent) -> PostgreSQL      |
|  - system metrics, network, USB, files      |------->|    DeviceHealthWorker, RetentionWorker       |
|  - heartbeat, clock-offset, policy sync     | pinned |  PostgreSQL (:5432)                          |
|  - SQLite queue  C:\ProgramData\NISS\Agent  |  cert  |  NISS Dashboard (Next.js :3000) -> SignalR   |
|  - uploader (ordered, retry, backoff)       |        +---------------------------------------------+
|        ^ named pipe (JSON lines)            |
| NISSMonitor.Session.exe (per interactive    |
|   session, user context)                    |
|  - foreground app / window title            |
|  - idle/active, lock/unlock                 |
|  - keyboard/mouse activity COUNTS (Raw Input)|
|  - open applications (window enumeration)   |
+--------------------------------------------+
```

## Projects

| Path | Role |
|---|---|
| `src/NISS.Monitor.Protocol` | The wire contract (event envelope, payloads, API DTOs). Shared by agent and server; mirrored in TypeScript for the dashboard. |
| `src/NISS.Monitor.Core` | Platform-neutral abstractions (`IProcessMonitor`, `IForegroundMonitor`, `IIdleMonitor`, `IInputActivityMonitor`, `ISystemMetricsCollector`, `IUsbMonitor`, `IFileActivityMonitor`, `IHeartbeatService`, `IEventQueue`, `IUploader`, `IDeviceEnrollmentService`, `IScreenCaptureService`, `IBrowserMonitoringProvider`) and the pure engines (application-session, presence, process diff, input buckets, clock offset, backoff). |
| `src/NISS.Monitor.Infrastructure` | Windows implementations: Win32 P/Invoke, SQLite queue, HTTPS client with certificate pinning, enrollment, DPAPI credential storage, named-pipe IPC, session launcher, setup helpers. |
| `src/NISS.Monitor.Service` | `NISSMonitor.Service.exe` - Worker Service host; one supervised worker per collector. |
| `src/NISS.Monitor.Session` | `NISSMonitor.Session.exe` - interactive-session process. |
| `src/NISS.Monitor.Cli` | `NISSAgent.exe` - administrator CLI. |
| `src/NISS.Monitor.Installer` | `NISSMonitorSetup.exe` - installer / repair / uninstall. |
| `server/*` | Clean-architecture API: Domain, Application, Infrastructure (EF Core + Npgsql), Workers, Api. |
| `web/niss-monitor-dashboard` | Next.js dashboard. |

## Why two processes on the endpoint

A Windows service runs in session 0 and has **no desktop**. Foreground window, idle time (per-session input),
lock state and raw input all belong to the *interactive* session. So:

* the **service** owns everything machine-wide, the durable queue and the network;
* the **session process** owns everything desktop-bound and streams it to the service over a named pipe;
* the service launches the session process into each interactive session with the documented
  `WTSQueryUserToken` + `CreateProcessAsUser` pattern (and restarts it with back-off). The installer also adds an HKLM `Run`
  entry; the session process is a per-session singleton (mutex), so both paths are harmless together.

The pipe is verified server-side: the client's Windows session id comes from the OS (not from the message), and by default
only the installed `NISSMonitor.Session.exe` is accepted. The session process may send only a whitelist of event types
(it cannot forge logins/logouts or process events).

## The four concepts that are never mixed

| Concept | Meaning | Source |
|---|---|---|
| RUNNING | the process exists | `PROCESS_STARTED/STOPPED` (service), `APPLICATION_STARTED/STOPPED` (window owners) |
| FOREGROUND | owns the foreground window | `APPLICATION_FOREGROUND/BACKGROUND` -> `application_sessions` |
| INTERACTION | mouse/keyboard input within the interaction window | `interaction_seconds`, `MOUSE_/KEYBOARD_ACTIVITY` |
| IDLE | no input for longer than the idle threshold | `IDLE_STARTED/IDLE_ENDED` |

`AppUsageTotal` (API) carries `runningSeconds`, `foregroundSeconds`, `interactionSeconds` separately, and the dashboard labels
them *Open/Running*, *Foreground*, *Actively used*. Nothing is labelled "productive".

## Application-session engine

`ApplicationSessionEngine` turns 1-second foreground samples into sessions ("Excel 09:00:10 -> 09:35:20 = 35m10s").
A session ends on app switch, lock, logout, process exit, or a long gap (suspend/hibernate). Title changes inside the same
process do not split it. It also accumulates `interactionSeconds` and `idleSeconds` per session. Only transitions are
emitted - never duplicate identical samples.

## Idle/active engine

`PresenceEngine` is driven by `GetLastInputInfo` ("time since last input"), so a lit screen is never mistaken for activity.
`IDLE_STARTED` carries `idleSinceUtc` = the time of the last *real* input. Locking ends an idle period (lock time is tracked as
`LOCKED`). The threshold (default 5 min) comes from policy.

## Offline-first data path

```
collector -> bounded Channel -> EventFactory (identity, sequence, clock offset) -> SQLite (events + upload_queue)
          -> Uploader (ordered by sequence) -> POST /events/batch -> per-event ACCEPTED | DUPLICATE | REJECTED
```

* Every event has a globally unique `EventId`; the server stores it in `event_ingestion` (primary key) and ignores repeats,
  so re-sending after a lost response is always safe.
* Failed batches back off exponentially (2 s ... 5 min, jitter). A backing-off head event blocks later events from overtaking it,
  so order is preserved. Permanently rejected events are dead-lettered so they cannot block the queue; a "poison" event in a
  batch is isolated by re-sending one by one.
* Process events use **deterministic** ids (`hash(processSessionId, type)`), so an agent restart never duplicates them.
* Pending cap (default 1M events): oldest low-value telemetry (mouse/keyboard/metrics) is dropped first.

## Time handling

All server timestamps are UTC (`timestamptz`). Each heartbeat measures `serverTime - deviceTime` (round-trip corrected, median of 5).
Events are stamped `deviceUtc + offset` and carry the offset. Durations are measured on the device from consecutive samples,
capped per tick, so a clock step or a sleep cannot inflate them. The event's local offset (`localTimestamp`) is kept for presentation.

## Failure isolation

Every collector runs under `CollectorWorker`: an exception is logged, the collector is restarted with exponential back-off, an
`AGENT_ERROR` is reported (throttled), and nothing else is affected. If USB monitoring fails, process monitoring continues; if the
server is down, collection continues; if the session process dies, the service continues and relaunches it.
Unclean shutdowns (crash, power loss) are detected on the next start (`clean_shutdown` flag) and reported in `AGENT_STARTED`.

## Health model (server)

`ONLINE` (heartbeat <= 60 s) -> `WARNING` (delayed) -> `OFFLINE` (> 5 min); `SERVICE_STOPPED` after a graceful `AGENT_STOPPED`;
`DISABLED` / `REVOKED` by administrators. Further detections: outdated version, session agent missing, backlog above threshold
(`AGENT_BACKLOG_WARNING`), upload failure, authentication failure. Thresholds are configurable.

## Privacy by construction

* Keyboard: Raw Input is used **only to count** key-down events. The `RAWKEYBOARD` virtual-key/scan-code fields are never read;
  `InputBucketAggregator.RecordKeyDown()` takes no arguments; the payload types contain only counts and timestamps. A unit test
  guards this.
* No global hooks, no injection, no clipboard, no webcam/microphone, no browser history/URLs.
* Screen capture (periodic screenshots, flagged-activity captures and clips, on-demand live view) ships **off** and is
  enabled per policy scope. It never captures while the workstation is locked or the user is idle, and reading a stored
  image is audited. Flagged sites are recognised from the window title, never from a URL. See
  [screen-monitoring.md](screen-monitoring.md).
* File monitoring is off by default, admin-configured paths only, metadata only.
* Window titles / command lines are collected only if the effective policy allows (`captureWindowTitles`, `captureCommandLines`).

See [security.md](security.md) for the security model.
