# API reference (v1)

Base URL: `https://<CENTRAL-PC>:8443/api/v1`. JSON, camelCase, enums as strings, timestamps ISO-8601 UTC.
The DTOs are defined **once**, in C#, in `src/NISS.Monitor.Protocol` (`AgentApi.cs`, `DashboardApi.cs`, `Payloads.cs`);
the dashboard mirrors them in `web/niss-monitor-dashboard/src/lib/types.ts`. Additive changes only.

Errors use RFC 7807 problem details (`application/problem+json`). Validation errors are HTTP 400 with an `errors` map.

## Authentication

| Caller | Scheme |
|---|---|
| Dashboard user | `Authorization: Bearer <JWT>` (15 min) + refresh token (rotating, 8 h). Roles `SUPERADMIN` > `ADMIN` > `VIEWER` |
| Endpoint agent | `Authorization: NissDevice <deviceId>:<deviceSecret>` (per-device secret, SHA-256 hash stored server-side) |
| Enrollment | one-time enrollment token in the request body |

Role rules: `VIEWER` = read-only monitoring data. `ADMIN` = viewer + device actions (disable/enable/revoke/re-enroll),
enrollment tokens, employees, settings, audit log, reports export. `SUPERADMIN` = admin + admin-user management.

Failed device authentication raises a `AUTH_FAILURE` security event (throttled per device) and returns 401.
Login and enrollment are rate limited (5 attempts / minute / IP for login).

## Agent endpoints

| Method | Path | Notes |
|---|---|---|
| POST | `/devices/enroll` | `EnrollRequest` -> `EnrollResponse`. Consumes a token. Same `MachineGuid` re-enrolls the existing device (new secret) if the token allows it. |
| POST | `/devices/credentials/rotate` | device auth. Returns new secret, old one invalid immediately. |
| POST | `/devices/heartbeat` (alias `/heartbeat`) | `HeartbeatRequest` -> `HeartbeatResponse`. Updates `heartbeats`, `agent_status`, live state; pushes `DeviceStateChanged`. |
| POST | `/events/batch` | `EventBatchRequest` (max 500 events) -> `EventBatchResponse`. Idempotent per `eventId`. Transactional per batch, processed in `sequence` order. |
| POST | `/metrics/batch` (alias `/metrics`) | same body; only `SYSTEM_METRIC` events are accepted here. |
| GET | `/agent/config` | `AgentConfigResponse` (effective policy: global < department < device). |
| POST | `/agent/status` | `AgentStatusRequest` -> 204. |
| GET | `/health` | anonymous `HealthResponse`. |

A device whose status is `DISABLED` or `REVOKED` gets 403 on ingest, and `deviceStatus` reflects it on heartbeat.

## Dashboard endpoints (JWT)

| Method | Path | Returns |
|---|---|---|
| POST | `/auth/login`, `/auth/refresh`, `/auth/logout` | `AuthResponse` |
| GET | `/auth/me` | `UserInfo` |
| GET/POST/PUT/DELETE | `/admin-users[/{id}]` | SUPERADMIN. `AdminUserDto` / `AdminUserUpsert` |
| GET | `/overview` | `OverviewDto` |
| GET | `/live` | `LiveDeviceCard[]` (query: `status`, `department`, `employee`, `search`) |
| GET | `/devices` | `Paged<DeviceSummary>` (`status`, `employee`, `department`, `search`, `page`, `pageSize`) |
| GET | `/devices/{deviceId}` | `DeviceDetail` |
| GET | `/devices/{deviceId}/activity` | `Paged<EventDto>` (`from`, `to`, `eventType` repeatable) |
| GET | `/devices/{deviceId}/applications` | `ApplicationActivityResponse` (`from`, `to`, `application`) |
| GET | `/devices/{deviceId}/processes` | `Paged<ProcessSessionDto>` (`from`, `to`, `status`=RUNNING\|STOPPED, `search`) |
| GET | `/devices/{deviceId}/metrics` | `SystemMetricDto[]` (`from`, `to`, `maxPoints`) |
| GET | `/devices/{deviceId}/timeline` | `TimelineItem[]` |
| GET | `/devices/{deviceId}/diagnostics` | `DeviceDiagnostics` |
| GET | `/devices/{deviceId}/diagnostics/download` | JSON attachment (ADMIN+) |
| POST | `/devices/{deviceId}/disable`, `/enable`, `/revoke` | ADMIN+, audited |
| POST | `/devices/{deviceId}/re-enroll` | ADMIN+, returns `EnrollmentTokenDto` (with token) bound to this device |
| GET | `/applications` | `ApplicationActivityResponse` across devices (`from`, `to`, `employee`, `department`, `device`, `application`, `category`) |
| GET | `/processes` | `Paged<ProcessSessionDto>` across devices |
| GET | `/employees` | `Paged<EmployeeDto>` (`search`, `department`) |
| POST/PUT | `/employees[/{id}]` | ADMIN+ `EmployeeUpsert` |
| GET | `/employees/{id}/summary` | `EmployeeDaySummary` (`date`, `tzOffsetMinutes`) |
| GET | `/employees/{id}/timeline` | `TimelineItem[]` (`from`, `to`) |
| GET | `/departments` | `string[]` |
| GET | `/usb-events`, `/file-events` | `Paged<UsbEventDto>` / `Paged<FileEventDto>` |
| GET | `/security-events` | `Paged<SecurityEventDto>` |
| POST | `/security-events/{id}/resolve` | ADMIN+ |
| GET | `/audit-logs` | `Paged<AuditLogDto>` (ADMIN+) |
| GET | `/reports/daily`, `/reports/{kind}` | `ReportResult`; `format=csv` returns text/csv. Kinds: `application-usage`, `active-idle`, `device-health`, `process-timeline`, `usb`, `agent-health`, `department-summary` |
| GET/PUT | `/settings/policies`, `/settings/policies/{id}` | `PolicyDto[]` / `PolicyUpsert` |
| GET/PUT | `/settings/app-categories` | `AppCategoryRule[]` (PUT replaces the list) |
| GET/PUT | `/settings/retention` | `RetentionSettings` |
| GET/PUT | `/settings/health-thresholds` | `HealthThresholds` |
| GET/POST/DELETE | `/enrollment-tokens[/{id}]` | ADMIN+ `EnrollmentTokenRequest` -> `EnrollmentTokenDto` |

### Query parameter conventions

* `from`/`to` are ISO-8601 instants. Default range is "today" in the server's local time zone.
* `date` = `yyyy-MM-dd`; `tzOffsetMinutes` (default: server local offset) says which local day it means.
* `page` (1-based) / `pageSize` (default 50, max 500).

## Real-time (SignalR)

Hub: `/hubs/monitor`, JWT via `access_token` query string for WebSockets. Constants in `HubMethods`.

| Direction | Method | Argument |
|---|---|---|
| server -> clients (group `all`) | `DeviceStateChanged` | `LiveDeviceCard` |
| server -> clients (group `all`) | `DeviceStatusChanged` | `DeviceStatusChange` |
| server -> clients (group `all`) | `SecurityEventRaised` | `SecurityEventDto` |
| server -> clients (groups `feed`, `device:{id}`) | `EventIngested` | `EventDto` (non-metric events only) |
| client -> server | `SubscribeDevice(deviceId)` / `UnsubscribeDevice(deviceId)` / `SubscribeFeed()` / `UnsubscribeFeed()` | |

Flow: API receives batch -> persists (transaction) -> after commit publishes to SignalR -> dashboard patches its state.

## Health computation (server worker, every 15 s)

| Condition | State |
|---|---|
| never heard from | `PENDING` |
| last heartbeat <= 60 s | `ONLINE` |
| 60 s < age <= 5 min | `WARNING` (heartbeat delayed) |
| age > 5 min | `OFFLINE` |
| last event `AGENT_STOPPED` and no newer heartbeat | `SERVICE_STOPPED` |
| admin disabled / revoked | `DISABLED` / `REVOKED` |

Thresholds are configurable (`/settings/health-thresholds`, `serversettings.json`). Transitions write a `security_events` row and
publish `DeviceStatusChanged`. Other detections: outdated agent version, session agent missing while a user is logged in,
`queueDepth` above `backlogWarningThreshold` -> `AGENT_BACKLOG_WARNING`, upload failures reported by the agent, authentication failures.

## Server behaviour details (implementation notes)

**Status codes.** 400 validation (`application/problem+json`, `errors` map keyed by camelCase field), 401 bad/missing credentials
(devices: `WWW-Authenticate: NissDevice`; an invalid, used or expired enrollment token is also 401), 403 role too low **or** a
disabled/revoked device on ingest / config / rotate, 404 unknown id, 409 conflicts (duplicate employee code / policy scope, last SUPERADMIN),
413 body > 5 MB, 429 rate limited (`Retry-After: 60`). A malformed *event* inside a batch never fails the batch: it is reported as
`REJECTED` in the per-event result list (batch-level problems - more than 500 events, `deviceId` not equal to the authenticated
device - are 400).

**Rate limits** (per minute; `RateLimits` in `serversettings.json`): login and refresh 5 per IP, enrollment 10 per IP,
ingest/heartbeat/config endpoints 1200 per device.

**Enrollment.** `POST /enrollment-tokens` returns the token once (`enr_...`); only its SHA-256 is stored. Consumption is atomic
(`UPDATE ... WHERE used_at IS NULL`), so a token can create exactly one device even when used concurrently. A token bound to a
device (`POST /devices/{id}/re-enroll` or `deviceId` in the request) re-enrolls that device, including a revoked one. Without a bound
token, the same `MachineGuid` re-enrolls its device only if the device is not DISABLED/REVOKED. A device secret is 32 random bytes
(base64url), returned once; the server keeps `SHA-256(secret)` and compares in constant time. `POST /devices/credentials/rotate`
invalidates the old secret immediately.

**Heartbeat.** Disabled/revoked devices get `200` with `deviceStatus` `DISABLED`/`REVOKED` and nothing is recorded. Otherwise the
response `deviceStatus` is `ACTIVE`, `configVersion` is the server-wide policy version (any policy change increments it) and
`minAgentVersion` comes from `/settings/health-thresholds`. Presence, foreground app and open apps are taken from `live`; the
dashboard card derives `running` (every open app), `foreground` (only the foreground process) and `interaction`
(foreground and `live.interacting`). Events older than the newest live information (heartbeat or newer event) never overwrite it.

**Time.** `timestampUtc` more than 5 minutes ahead of the server clock is replaced by the receive time and a `CLOCK_SKEW` security
event is raised (at most one per device per hour). Default `from`/`to` is "today" in the server's local zone; `date` +
`tzOffsetMinutes` (minutes **east** of UTC) select a local day. Active/idle: logged-in intervals (`user_sessions`) minus the union of
`idle_sessions` (IDLE, LOCKED, DISCONNECTED) intervals, clipped to the window; the categories never overlap, so
`loggedIn = active + idle + locked`. An unclosed session ends at *now*, but never later than the device's last heartbeat.

**Application totals.** `runningSeconds` = union of process lifetimes (`process_sessions`, at least the foreground time),
`foregroundSeconds` / `interactionSeconds` = `application_sessions` (sessions crossing the window edge are clipped proportionally),
`launches` = `APPLICATION_STARTED` events, `foregroundTransitions` = foreground sessions that started in the window. Process names
are grouped without their `.exe` suffix. Categories come from `/settings/app-categories` (case-insensitive wildcards `*` and `?`,
highest priority first, then the longest pattern; default `Unknown`). Never a productivity score.

**Reports.** `ReportColumn.type` is one of `string`, `number`, `datetime`, `duration` (seconds). Kinds and columns:
`daily` (employeeCode, employee, department, firstLogin, lastLogout, loggedInSeconds, activeSeconds, idleSeconds, lockedSeconds,
topApplication, devices), `application-usage`, `active-idle` (per employee per local day), `device-health` (device, hostname,
employeeCode, status, lastSeen, agentVersion, heartbeats, queueDepth, avgCpuPercent, maxCpuPercent, avgMemoryPercent,
diskUsedPercent, offlineEvents), `process-timeline`, `usb`, `agent-health`, `department-summary`. `format=csv` requires ADMIN and is
written to the audit log; cells that start with `= + - @` are prefixed with `'` (spreadsheet formula injection).

**SignalR.** Every authenticated client is in group `all`. `EventIngested` goes to group `feed` and to `device:{deviceId}`; a client
that joined both groups receives it twice (dedupe on `eventId`). Notifications are published only after the database commit.

**Audit actions** (all admin actions): `LOGIN`, `LOGIN_FAILED`, `LOGOUT`, `DEVICE_ENROLLED`, `DEVICE_DISABLED`, `DEVICE_ENABLED`,
`DEVICE_REVOKED`, `DEVICE_REENROLL_REQUESTED`, `DEVICE_SECRET_ROTATED`, `ENROLLMENT_TOKEN_CREATED`, `ENROLLMENT_TOKEN_DELETED`,
`EMPLOYEE_CREATED`, `EMPLOYEE_UPDATED`, `POLICY_UPDATED`, `POLICY_DELETED`, `SETTINGS_UPDATED`, `APP_CATEGORIES_UPDATED`,
`DIAGNOSTICS_DOWNLOADED`, `REPORT_EXPORTED`, `ADMIN_USER_CREATED/UPDATED/DELETED`, `SECURITY_EVENT_RESOLVED`.

## Configuration (`server/NISS.Api/serversettings.json`)

Sources, later wins: `serversettings.json` -> `serversettings.local.json` (git-ignored, for secrets) -> environment variables with the
`NISS_` prefix (`NISS_Database__ConnectionString`) -> command line. Sections: `Server`, `Dashboard`, `Database`, `Security`,
`Monitoring`, `Retention` (as specified) plus `RateLimits` (`LoginPerMinute` 5, `RefreshPerMinute` 60, `EnrollPerMinute` 10, `IngestPerMinute` 1200) and
`Workers` (`Enabled`, `HealthIntervalSeconds`, `RetentionHourLocal`, `TokenCleanupIntervalMinutes`). Kestrel binds
`BindIp:Port`; HTTPS with `Certificate` (PFX or store thumbprint); without a certificate the server refuses to start unless
`AllowInsecureHttp` is true. CORS allows any origin whose port equals `Dashboard:Port` (headers any, credentials allowed).
`--migrate` applies migrations and exits.

## Changelog

The Protocol project was **not** changed (no fields or types added, renamed or removed). Additions to this contract made by the server:

* `PUT /settings/policies` (collection) creates a policy, like `POST /settings/policies`; `DELETE /settings/policies/{id}` removes a
  non-global policy (GLOBAL cannot be deleted, exactly one exists).
* `GET /employees/{id}` returns `EmployeeDto`; `GET /enrollment-tokens` returns `EnrollmentTokenDto[]` (newest 200, `token` null);
  `GET /admin-users` returns `AdminUserDto[]`; `POST /security-events/{id}/resolve` returns the updated `SecurityEventDto`.
* `POST /auth/logout` returns 204; the body `{ "refreshToken": "..." }` is optional and revokes that refresh token.
* `ReportColumn.type` value `duration` (seconds) added to the documented set.
* Extra configuration sections `RateLimits` and `Workers`.
* `POST /auth/refresh` has its own limiter (`RateLimits:RefreshPerMinute`, default 60/min/IP) instead of sharing the 5/min login limiter:
  the dashboard refreshes on every page load, and refresh tokens are high-entropy. Found by driving the dashboard against the real API.
