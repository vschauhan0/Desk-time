# Testing guide

## Automated tests

| Suite | Command | Needs | Covers |
|---|---|---|---|
| Agent | `dotnet test src\NISS.Monitor.Tests` | Windows only (no admin, no DB) | engines, SQLite queue, offline/reconnect/duplicates, heartbeat + clock offset, enrollment (DPAPI), authentication failures, disabled device, collector supervision, unclean-shutdown detection (service restart), named-pipe IPC incl. session-process restart, real Win32 process/foreground/idle/metrics/WTS/USB smoke tests |
| Server | `dotnet test server\NISS.Api.Tests` | PostgreSQL (`NISS_TEST_DB`, default `127.0.0.1:5433/niss_monitor_test`) | enrollment, device + admin authentication, validation, heartbeat, idempotent ingestion, out-of-order/backlog, projections, health worker, SignalR delivery, persistence, reports, audit, rate limit |
| Dashboard | `cd web\niss-monitor-dashboard; npm test` | Node | formatters, API client, filters |
| End-to-end | `.\deployment\scripts\Test-EndToEnd.ps1` | PostgreSQL, .NET, an interactive desktop | real agent -> real API -> real PostgreSQL, offline/reconnect, stop/start, SignalR |

Coverage of the spec's test list: process start/stop (`Toolhelp_snapshot_sees_a_started_process_then_its_stop`, `Process_monitor_emits_...`),
foreground detection (`Foreground_and_open_window_detection_...`, `ApplicationSessionEngineTests`), idle (`PresenceEngineTests`), login/logout
(`SessionDiffEngineTests`), lock/unlock (`Lock_ends_the_session_...`, `Lock_while_idle_...`), mouse/keyboard metrics (`InputAggregatorTests`, privacy guard),
SQLite queue, offline mode + reconnect + duplicates (`OfflineFirstUploadTests`), heartbeat (`HeartbeatTests`), enrollment/authentication (`EnrollmentTests`,
`Authentication_failure_...`), service restart (`Queue_survives_a_service_restart`, `Unclean_shutdown_is_reported_...`), session-agent restart
(`Session_process_events_and_state_reach_the_service_and_survive_a_service_restart`).

## Manual scenario (28 steps)

Run on a test PC with the central server up. "Verify" = what to look at.

| # | Step | Verify |
|---|---|---|
| 1 | Start the central server: `.\NISS.Server.Start.ps1 ...` | health checks green, URLs printed |
| 2 | Open the dashboard `http://<central-pc>:3000`, sign in | Overview loads |
| 3 | Dashboard -> Devices -> Enrollment tokens -> new token (bind `EMP1024`) | token shown once |
| 3b | Install the agent on the test PC with that token | installer prints 12 OK steps; `NISSAgent --status` enrolled |
| 4 | Enroll (done by the installer) | device `PC-###` appears in Devices, status ONLINE within ~30 s |
| 5 | Log in as the test employee | `USER_LOGIN`, session agent running, Live card shows the user |
| 6-7 | Open Chrome, open Excel | Live: both under Applications = **RUNNING**; Process page lists CHROME.EXE / EXCEL.EXE with PID, parent, start |
| 8-9 | Switch Excel -> Chrome -> Excel | Application Activity: sessions (not samples) with durations; only the front app shows **FOREGROUND**; totals separate *Running* vs *Actively used* |
| 10 | Leave the PC idle for 5 min | `IDLE_STARTED` (idle since the last input), card shows IDLE; app is still RUNNING/FOREGROUND but not **INTERACTING** |
| 11-12 | Move the mouse, type text | `IDLE_ENDED` + `ACTIVE_STARTED`; `MOUSE_ACTIVITY` / `KEYBOARD_ACTIVITY` counts; **no typed characters anywhere** (check `input_activity` columns) |
| 13-14 | Open then close Outlook | `APPLICATION_STARTED/STOPPED`, process start/stop, launch counted |
| 15-16 | Plug / unplug a USB device | USB Events page: `USB_CONNECTED` / `USB_DISCONNECTED` with vendor/product |
| 17 | Disconnect the network | `NETWORK_DISCONNECTED` queued locally; card turns WARNING then OFFLINE |
| 18 | Keep working (open/close apps, lock/unlock) | `NISSAgent --status` queue depth grows |
| 19-20 | Restore the network | queue drains, oldest first; dashboard backfills the timeline; no duplicates (event ids identical) |
| 21-22 | `Stop-Service NISSMonitorService` | dashboard: SERVICE_STOPPED immediately (graceful `AGENT_STOPPED`); after 5 min OFFLINE alert |
| 23-24 | `Start-Service NISSMonitorService` | ONLINE again, next `AGENT_STARTED`, monitoring resumes; session agent re-attaches |
| 25-26 | Restart Windows | service starts automatically; session agent starts at logon; `AGENT_STARTED` with *unexpected previous shutdown = false* (clean) |
| 27 | Compare event timestamps with wall-clock | correct in UTC; local time shown in the viewer's zone; drift shown in Diagnostics |
| 28 | Watch Live Monitoring while doing the above | cards and the event feed update without page refresh (SignalR) |

`Test-EndToEnd.ps1` automates the parts that do not need a human or admin rights (steps 4, 6-9 partially, 11-13, 17-24, 28).
