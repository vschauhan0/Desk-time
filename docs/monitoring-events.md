# Monitoring event reference

All collectors emit the **common envelope** (`NISS.Monitor.Protocol.EventEnvelope`):

```json
{
  "eventId": "8c1f0e0a-7f37-4c9a-9d5e-6f0f3e7d2b11",
  "schemaVersion": 1,
  "deviceId": "PC-024",
  "employeeId": "EMP1024",
  "sessionId": "0f7e...guid",
  "eventType": "APPLICATION_FOREGROUND",
  "timestampUtc": "2026-09-21T09:35:20.412Z",
  "localTimestamp": "2026-09-21T15:05:20.412+05:30",
  "source": "NISSMonitor.Session",
  "sequence": 10452,
  "clockOffsetMs": -180,
  "payload": { }
}
```

* `timestampUtc` = device UTC clock **plus** the agent's measured offset to the server clock (see *Time handling*).
* `sequence` is a per-device monotonic counter taken from the agent's local SQLite queue; uploads are
  ordered by it.
* `eventId` is the idempotency key. The server stores it in `event_ingestion` (primary key) and ignores repeats.

## The four concepts that are never mixed

| Concept | Meaning | Where it is recorded |
|---|---|---|
| **RUNNING** | the process exists | `PROCESS_STARTED/STOPPED` -> `process_sessions`; `APPLICATION_STARTED/STOPPED` for window-owning apps |
| **FOREGROUND** | the process owns the foreground window | `APPLICATION_FOREGROUND/BACKGROUND` -> `application_sessions.duration_seconds` |
| **INTERACTION** | mouse/keyboard input within the interaction window (default 30 s) | `application_sessions.interaction_seconds`, `MOUSE_ACTIVITY`, `KEYBOARD_ACTIVITY` -> `input_activity` |
| **IDLE** | no input for longer than the idle threshold (default 5 min) | `IDLE_STARTED/IDLE_ENDED` -> `idle_sessions` (kind `IDLE`) |

Example: Chrome = running yes / foreground no / interaction no; Excel = running yes / foreground yes / interaction yes.

## Event catalogue

| Event type | Emitted by | Payload record | Server projection |
|---|---|---|---|
| `PROCESS_STARTED` | Service | `ProcessPayload` | insert `process_events`; upsert `process_sessions` (key `ProcessSessionId`) |
| `PROCESS_STOPPED` | Service | `ProcessPayload` (with `EndedUtc`) | insert `process_events`; close `process_sessions` |
| `APPLICATION_STARTED` | Session | `ApplicationLifecyclePayload` | timeline; counts a *launch* in usage totals |
| `APPLICATION_STOPPED` | Session | `ApplicationLifecyclePayload` | timeline |
| `APPLICATION_FOREGROUND` | Session | `ApplicationForegroundPayload` | insert open `application_sessions` row (key `AppSessionId`, `ended_at` null); update live state |
| `APPLICATION_BACKGROUND` | Session | `ApplicationBackgroundPayload` | complete `application_sessions` row: `ended_at`, `duration_seconds`, `interaction_seconds`, `idle_seconds` |
| `USER_LOGIN` / `USER_LOGOUT` | Service (WTS polling) | `SessionStatePayload` | open/close `user_sessions`; ensure `device_users` row |
| `SESSION_CONNECT` / `SESSION_DISCONNECT` | Service | `SessionStatePayload` | `idle_sessions` kind `DISCONNECTED` |
| `SESSION_LOCK` / `SESSION_UNLOCK` | Session | `SessionStatePayload` | `idle_sessions` kind `LOCKED` |
| `ACTIVE_STARTED` | Session | `IdlePayload` | live state |
| `IDLE_STARTED` | Session | `IdlePayload` (`IdleSinceUtc` = last real input) | open `idle_sessions` kind `IDLE` starting at `IdleSinceUtc` |
| `IDLE_ENDED` | Session | `IdlePayload` (`IdleSeconds`) | close it |
| `MOUSE_ACTIVITY` / `KEYBOARD_ACTIVITY` | Session | `InputActivityPayload` | upsert `input_activity` by `(device, BucketStartUtc)` |
| `USB_CONNECTED` / `USB_DISCONNECTED` | Service | `UsbPayload` | insert `usb_events` |
| `FILE_CREATED/MODIFIED/RENAMED/DELETED` | Service | `FilePayload` | insert `file_events` (metadata only) |
| `SYSTEM_METRIC` | Service | `SystemMetricPayload` | insert `system_metrics` |
| `NETWORK_CONNECTED` / `NETWORK_DISCONNECTED` | Service | `NetworkPayload` | timeline; live state |
| `AGENT_STARTED` | Service | `AgentPayload` | `agent_status`; if `UnexpectedPreviousShutdown` -> security event `UNEXPECTED_SHUTDOWN` |
| `AGENT_STOPPED` | Service | `AgentPayload` | device state `SERVICE_STOPPED` |
| `AGENT_ERROR` | Service/Session | `AgentPayload` | `agent_status.last_error`; security event `COLLECTOR_FAILURE` |
| `AGENT_VERSION_CHANGED` | Service | `AgentPayload` | updates `devices.agent_version` |
| `AGENT_HEARTBEAT` | (reserved) | `AgentPayload` | heartbeats use `/devices/heartbeat` |

Every event, whatever its type, is also stored in `event_ingestion` (id, device, type, timestamps, payload jsonb)
so the raw stream can be queried and retention-pruned independently of the projections.

## Privacy guarantees encoded in the schema

* `InputActivityPayload` contains **counts and timestamps only**. There is no field that could hold a key,
  a character, a scan code, a clipboard value or a window's text content.
* `FilePayload` carries path metadata only, never contents.
* `windowTitle` is sent only when the effective policy has `captureWindowTitles = true`.
* `commandLine` is sent only when `captureCommandLines = true` (default false).
* Browser history / URLs are not collected (V1). `IBrowserMonitoringProvider` reports *Not Enabled*.

## Time handling

1. All server timestamps are stored as UTC (`timestamptz`).
2. Each event carries the device's local UTC offset in `localTimestamp` so the UI can present local time.
3. The agent measures `offset = serverTime - deviceTime` from every heartbeat response (half-RTT corrected) and
   stamps events with `deviceUtcNow + offset`. The applied offset is sent as `clockOffsetMs`.
4. Durations (`durationSeconds`, `interactionSeconds`, `idleSeconds`, `idleSeconds` in `IdlePayload`) are measured on
   the device with a monotonic clock, so a clock step cannot corrupt them.
5. The server rejects events whose `timestampUtc` is more than 5 minutes in the future (they are stored with the
   receive time instead and a `CLOCK_SKEW` security event is raised).
