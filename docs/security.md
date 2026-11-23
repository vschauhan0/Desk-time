# Security guide

This is an **authorized, transparent** monitoring product. The agent is a normal Windows service visible in `services.msc`,
installed under Program Files, removable by administrators. It contains no rootkit, stealth, kernel-bypass, credential-theft or
anti-admin mechanisms and never defeats Windows security controls.

## Threat model in one page

| Asset | Protection |
|---|---|
| Monitoring data in transit | HTTPS (TLS). Agents **pin** the server certificate by SHA-256 thumbprint, so a self-signed LAN certificate is validated strictly - TLS validation is never disabled. |
| Device identity | Per-device secret (32 random bytes) issued at enrollment; the server stores only its SHA-256 hash and compares in constant time. No shared API key exists. |
| Secret at rest on the PC | Stored in `agent.db` encrypted with machine-scope DPAPI; the data folder is ACL'd to SYSTEM + Administrators. |
| Enrollment | One-time, expiring token created by an admin (optionally bound to an employee / an existing device for re-enrollment). |
| Dashboard access | Admin login (PBKDF2 hashed passwords), short-lived JWT (15 min) + rotating refresh token, roles `SUPERADMIN` / `ADMIN` / `VIEWER`, rate-limited login. |
| Admin actions | Every login, device action, token creation, settings change, export and diagnostics download is written to `audit_logs`. |
| Input validation | FluentValidation on every request, request-size limits (batch <= 500 events / 5 MB), parameterised EF Core queries only. |
| Logs | Serilog with a redaction enricher (properties named secret/token/password/authorization are masked; `NissDevice ...` / `Bearer ...` values scrubbed). Tokens, secrets and typed text are never logged. |

## Device lifecycle

`enroll` -> `heartbeat/ingest` -> `rotate` (device-initiated, old secret invalid immediately) -> `disable` / `enable` (reversible) ->
`revoke` (permanent; needs a re-enroll token to come back) -> `re-enroll`. A disabled/revoked device gets HTTP 403 on ingest and,
via the heartbeat response, stops collecting and uploading.

## Session-process channel

Named pipe `\\.\pipe\NISSMonitor.Session` (ACL: SYSTEM/Administrators full, authenticated users read/write). The service verifies each
client: OS-reported session id and process image (must be the installed `NISSMonitor.Session.exe` in the service's directory),
caps message size, and accepts only a whitelist of event types. A local user can therefore not impersonate another session or forge
login/logout/process events. (A local user *can* stop their own session process - see "Known limitations".)

## Server hardening checklist

1. Replace the bootstrap admin password immediately (dashboard -> Settings -> Admin users).
2. Keep `serversettings.local.json` (database password, certificate password, JWT key) out of source control (git-ignored) and readable only by the account running the API.
3. Restrict Windows Firewall inbound rules for 8443/3000 to your LAN/management subnet.
4. Use a dedicated PostgreSQL role for NISS with only its database; do not use `postgres` in production.
5. Back up the PostgreSQL database; audit logs live there too.
6. Trust `server.cer` on admin PCs (`Trust-NissCertificate.ps1`) so browsers show no warning.
7. Give the VIEWER role to people who only need to read; ADMIN only where actions are required.

## Preventing unauthorised removal (organization policy)

The agent deliberately does **not** try to hide or protect itself from administrators. Restrict removal with standard Windows management:

* Employees should be **standard users**: they cannot stop/delete the service or write to `C:\Program Files\NISS` or `C:\ProgramData\NISS`.
* Deploy/upgrade/remove only through your management tooling (Intune, GPO, SCCM, RMM) using `NISSMonitorSetup.exe` or `Install.ps1`.
* Optionally restrict the service ACL via GPO (Computer Configuration > Windows Settings > Security Settings > System Services >
  NISSMonitorService > Security) so only Administrators/SYSTEM can control it.
* Use AppLocker / WDAC to allow only signed NISS binaries (sign the binaries with your code-signing certificate).
* Use the dashboard's health view: a stopped/uninstalled agent shows as `SERVICE_STOPPED` or `OFFLINE`, and an unclean stop is reported on the next start.

## Data minimisation and transparency

Tell employees what is collected (see [monitoring-events.md](monitoring-events.md)) - typically via your acceptable-use policy and
works-council/legal process as your jurisdiction requires. Retention is configurable per data class (defaults: raw events 90 days,
application summaries 1 year, device health 180 days, audit logs 1 year).

## Mutual watchdog (resilience against a casual stop)

Two ordinary Windows services are installed: `NISSMonitorService` (the agent) and `Watchdog` (a small
companion - its Properties/Description in `services.msc` names it as the NISS Monitor watchdog, so it stays
identifiable to anyone who opens it, even though its display name is generic). Each polls the other's status every
5 seconds and starts it again if it is ever found stopped. This covers a casual stop through Task Manager,
Services.msc, or `sc stop` on *either* service - something Windows' built-in "restart on failure" recovery does
**not** cover, because that mechanism only fires for crashes, not for a deliberate stop request. Measured in
practice: a killed service is back within 5-13 seconds, well under the two services' combined ~13-second worst case
for the session tracker and well inside a 15-second target.

Both services remain completely ordinary and visible - `sc query`, Services.msc, and Task Manager's Services tab all
show both, with normal descriptions. This is not tamper protection: an administrator who wants to stop monitoring
permanently still can, by disabling (Startup type: Disabled) or deleting both services, or by running the official
uninstaller. It only defeats a single casual "End Task" on one of the two processes.

## Known limitations

* A local administrator can permanently disable or remove the agent (by design - see above). The server detects and shows it, but cannot prevent it.
* A standard user can end their own `NISSMonitor.Session.exe` process; the service notices (session agent missing) and relaunches it within seconds.
* A local administrator can stop either `NISSMonitorService` or `Watchdog` via Task Manager - the mutual watchdog restarts it within one poll interval (~5-13 s), but stopping *both* at the same instant (or disabling either one's Startup type) is not something the other can recover from, by design (see above).
* Process start/stop uses a 2-second snapshot diff: processes that live < 2 s can be missed.
* The service does not sign its binaries; sign them before wide rollout.
