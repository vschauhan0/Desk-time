# Developer guide

## Repository layout

```
NISS-PC-Monitor/
  src/            agent-side projects (see architecture.md)
  server/         API (Domain / Application / Infrastructure / Workers / Api / Api.Tests)
  web/            Next.js dashboard
  deployment/     install, uninstall, database, certificates, scripts
  docs/           this documentation
  NISS-PC-Monitor.slnx   root solution (agent + server projects)
  NISS.Server.Start.ps1  one-command server launcher
```

## Toolchain

.NET SDK 10.0.x, Node.js 20+ (24 tested), PostgreSQL 14+. `Directory.Build.props` enables nullable reference types,
warnings-as-errors, analysis level *latest* and XML documentation for every project.

## Build and test

```powershell
dotnet build NISS-PC-Monitor.slnx
dotnet test  src\NISS.Monitor.Tests            # agent (no elevation, no database needed)
dotnet test  server\NISS.Api.Tests             # server (needs PostgreSQL; NISS_TEST_DB overrides the connection string)
cd web\niss-monitor-dashboard; npm test; npm run lint; npm run build
```

## Adding a collector (agent)

1. Define/extend the payload record in `NISS.Monitor.Protocol/Payloads.cs` and the event type constant in `EventTypes.cs` (add it to `EventTypes.All`).
2. Implement `ICollector` (or a specific interface such as `IUsbMonitor`) in `NISS.Monitor.Infrastructure/Collectors`. Emit through `IEventSink` only.
   Keep pure decision logic in `NISS.Monitor.Core/Engines` so it is unit-testable without Windows.
3. Register it in `ServiceCollectionExtensions` and add `AddCollector<T>(services)` in `NISS.Monitor.Service/Program.cs`. `CollectorWorker` gives you
   supervision, restart with back-off and health reporting for free.
4. Add the server projection in `NISS.Application` (event -> table) and document it in `monitoring-events.md`.
5. Session-side (desktop) data goes in `NISS.Monitor.Session/SessionRuntime.cs` and must be added to the pipe whitelist in `SessionPipeServer`.

## Conventions

* Async all the way with `CancellationToken`; no thread per process/event; bounded channels between producers and consumers.
* Every external call has a timeout, cancellation and back-off; network failures are *values* (`ApiResult`), not exceptions.
* One collector failing must never stop the agent - throw from `RunAsync` and let the supervisor restart it.
* Never log secrets, tokens or monitored content. Log message templates, not interpolated strings.
* Privacy: the input API takes no key information; keep it that way (there is a test that enforces it).
* Public interfaces have XML docs. Program.cs files only compose; behaviour lives in classes.

## Wire contract

`src/NISS.Monitor.Protocol` is the single source of truth. Changes must be additive (never rename/retype existing members).
The dashboard mirrors the DTOs in `web/niss-monitor-dashboard/src/lib/types.ts`.

## Running the agent from source (no installation, no elevation)

```powershell
$env:NISS_AGENT_DATA   = "$env:TEMP\niss-agent"                   # use a scratch data folder instead of C:\ProgramData
$env:NISS_Agent__Server__BaseUrl = 'https://localhost:8443'
$env:NISS_Agent__Enrollment__Token = '<token>'                  # unattended enrollment
$env:NISS_Agent__Session__RequireTrustedClientPath = 'false'   # dev only
dotnet run --project src\NISS.Monitor.Service                  # console host
dotnet run --project src\NISS.Monitor.Session                  # in another terminal (interactive desktop)
```

In console mode the service does not launch session agents (that needs LocalSystem); start the session process yourself.
