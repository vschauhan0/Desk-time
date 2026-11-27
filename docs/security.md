# Security guide

This is an **authorized, transparent** monitoring product. The agent is a normal Windows service visible in `services.msc`,
installed under Program Files. It contains no rootkit, stealth, kernel-bypass, credential-theft or code-injection mechanisms
and never defeats Windows security controls. By default the installer applies a **hardened service ACL** (a standard,
documented Windows access-control measure - not stealth) so the services cannot be stopped or deleted with the usual tools by
a local administrator; an administrator can still re-assert control deliberately and remove the agent via the official
uninstaller. See [Preventing unauthorised removal](#preventing-unauthorised-removal-defence-in-depth) and
[least-privilege-rollout.md](least-privilege-rollout.md).

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

## Preventing unauthorised removal (defence in depth)

No software can make itself truly unkillable by someone holding local-administrator rights on the machine - not this agent,
not commercial EDR. A determined admin always wins eventually (safe mode, offline disk, resetting the service ACL). The
dishonest alternative - a rootkit that hides from or fights the machine's owner - would get the agent flagged as malware and
destroy the lawful, *disclosed*-monitoring basis this product depends on, so it is deliberately out of scope. The goal is
therefore **"can't be killed by people who shouldn't, and instantly caught when someone who can does"**, delivered in layers:

1. **Least privilege (the real fix).** A standard (non-admin) user cannot stop/delete the services, kill the SYSTEM
   processes, or write to `C:\Program Files\NISS` / `C:\ProgramData\NISS`. Removing local-admin rights from employees makes
   the agent genuinely unremovable for everyone who does not need admin. Audit who still has it with
   `deployment\scripts\Audit-LocalAdmins.ps1`; the full playbook (including technical staff who need admin) is in
   [least-privilege-rollout.md](least-privilege-rollout.md).
2. **Hardened service ACL (on by default).** The installer applies a DACL that denies **STOP** and **DELETE** to
   Administrators on both services, so Services.msc, Task Manager's Services tab, `net stop`, `sc stop` and `sc delete` are
   refused even to an admin. SYSTEM keeps full control (so SCM recovery and the peer watchdog still work), and Administrators
   keep WRITE_DAC/change-config so the official uninstaller/repair - and a deliberate admin who runs `sc sdset` - can still
   re-assert control. This is a **speed bump plus detection, not a wall**: it defeats every casual/one-click stop and forces a
   deliberate, logged command-line act. Opt out with `NISSMonitorSetup.exe --no-service-hardening` or `Install.ps1
   -NoServiceHardening`.
3. **Tamper detection (catches the rest).** The dashboard health view already shows a stopped/uninstalled agent as
   `SERVICE_STOPPED` / `OFFLINE` and reports an unclean stop on the next start. Active, attributed tamper alerting (which PC,
   which user, when) is layer 3 of the anti-tamper work.
4. **Signed binaries + allow-listing.** Sign the binaries with your code-signing certificate and use AppLocker / WDAC to run
   only signed NISS binaries, so the executables cannot be replaced or silently swapped.

Deploy/upgrade/remove only through your management tooling (Intune, GPO, SCCM, RMM) using `NISSMonitorSetup.exe` or
`Install.ps1`. Set local-admin membership **explicitly** via Restricted Groups / Intune so a demoted user cannot simply
re-add themselves.

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
show both, with normal descriptions. The watchdog is complementary to the hardened service ACL above: the ACL denies an
admin the SCM **Stop**/**Delete** (Services.msc, `net stop`, `sc stop`, `sc delete`), while the watchdog covers a
process-level **End Task** (which terminates the process rather than stopping the service, and is a different right the ACL
does not govern). Together they defeat every one-click GUI route. Neither is a wall: a deliberate admin can still disable
(Startup type: Disabled) + reboot, reset the ACL with `sc sdset`, or run the official uninstaller - all deliberate,
command-line acts that tamper detection (layer 3) is there to catch.

## Known limitations

* A local administrator can still *deliberately* remove the agent (reset the service ACL with `sc sdset`, disable + reboot, or run the official uninstaller) - by design; the hardened ACL blocks the casual one-click routes, not a determined admin. The server detects and shows it, but cannot prevent it. The durable fix is to not grant employees local admin ([least-privilege-rollout.md](least-privilege-rollout.md)).
* With service hardening on (default), an admin's Services.msc/`net stop`/`sc stop`/`sc delete` is refused; a process-level "End Task" still terminates the process, and the mutual watchdog restarts it within one poll interval (~5-13 s). Ending *both* processes at the same instant, or disabling a Startup type, is a deliberate act the watchdog cannot recover from - caught by detection, not prevented.
* A standard user can end their own `NISSMonitor.Session.exe` process; the service notices (session agent missing) and relaunches it within seconds.
* Process start/stop uses a 2-second snapshot diff: processes that live < 2 s can be missed.
* The service does not sign its binaries; sign them before wide rollout.
