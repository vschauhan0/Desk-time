# Anti-tamper design (making the agent hard to kill, honestly)

**Date:** 2026-11-26
**Context:** The agent is disclosed, company-PC monitoring. Employees — including technical staff with local admin —
are stopping/uninstalling it. Goal: make it unkillable by people who shouldn't be able to, and instantly caught when
someone who can does — **without** a rootkit (which would make it malware and destroy the lawful, disclosed basis).

## Non-goals (hard boundary)

No rootkit, process/file hiding, kernel driver, anti-admin stealth, disabling the user's security tools, or any
deception of the machine's legitimate administrator. A determined local admin can always remove software from their
own machine; the design accepts that ceiling and covers it with detection + policy, not stealth.

## Four layers

| # | Layer | Status | Mechanism |
|---|---|---|---|
| 1 | Least privilege | **built** | `Audit-LocalAdmins.ps1` + [least-privilege-rollout.md](../../least-privilege-rollout.md). A non-admin cannot touch the agent at all. The real fix. |
| 2 | Service-ACL hardening | **built** | `AgentSetup.HardenServiceAcl`: DACL denies STOP+DELETE to Administrators (keeps SYSTEM full control + admin WRITE_DAC for deliberate override). On by default; `--no-service-hardening` opts out. |
| 3 | Tamper alerting | **this doc** | Attribute + surface stops/removals as prominent, acknowledgeable dashboard alerts. |
| 4 | Signing + allow-listing | **built (guide)** | `Sign-Agent.ps1` + [code-signing-and-allowlisting.md](../../code-signing-and-allowlisting.md). Stops binary swaps. |

## Layer 3 — tamper alerting (delivery: dashboard-only, chosen by the user)

### What already exists (reuse, do not rebuild)

* `SecurityEvent` entity (Kind, Severity, Message, RaisedAt, ResolvedAt) — persisted, with resolve == acknowledge.
* `ISecurityEventService.RaiseAsync` (throttle / only-if-none-open) and `ResolveByIdAsync` (audited).
* Live push: every raised event already goes to all dashboards over SignalR (`HubMethods.SecurityEventRaised`).
* Dashboard Security page (`features/security/`) lists + resolves events.
* Two tamper paths already fire a server signal today:
  * **Graceful stop** (`net stop`/`sc stop`/Services.msc) → agent emits `AGENT_STOPPED` → server shows `ServiceStopped` (Warning).
  * **Kill / crash / power-loss** → next start reports unclean → server raises `UnexpectedShutdown` (Warning).

### The three gaps this layer closes

1. **Attribution ("who").** The agent does not say which interactive user was present when it was stopped.
2. **Deliberate vs. benign.** A nightly PC shutdown and a deliberate `sc stop` both look identical today
   (`ServiceStopped`). Escalating both would cause alert fatigue; we must tell them apart.
3. **Prominence.** A tamper event is just another row on a page nobody is watching. It should push a toast and a
   banner.

### Design

**Agent (beacon).** In `AgentLifecycleWorker.StopAsync`, before the final upload:
* Determine **isShuttingDown** via Win32 `GetSystemMetrics(SM_SHUTTINGDOWN=0x2000)` — true for OS shutdown/restart.
* Determine the **interactive console user** via WTS (`WTSGetActiveConsoleSessionId` + `WTSQuerySessionInformation`),
  running as SYSTEM.
* Populate a new, optional `AgentPayload.InteractiveUser` **only when the stop is deliberate** — i.e. the OS is *not*
  shutting down **and** a console user is present. A benign shutdown leaves it null. Set `Reason` to `"SHUTDOWN"` vs
  `"SERVICE_STOP"` accordingly.
* The decision is a pure function `StopClassifier.Classify(isShuttingDown, consoleUser)` so it is unit-testable
  without a real service. (A hard process-kill never reaches `StopAsync`; that path stays the `UnexpectedShutdown`
  signal, which we enrich with the last-known user server-side.)

Backward-safe: an older agent never sets `InteractiveUser`, so it never trips a false tamper alert — the server keys
the escalation on the field being present.

**Server (projector).** In `AgentEventProjector`:
* `AGENT_STOPPED` with a non-empty `InteractiveUser` → raise new kind **`TAMPER_SUSPECTED`** (Critical):
  *"{device}: monitoring service was stopped while {user} was signed in."* Still set `ServiceStoppedAt` / status.
* `AGENT_STOPPED` without it (benign shutdown / old agent) → unchanged quiet `ServiceStopped` (Warning).
* `AGENT_STARTED` with `UnexpectedPreviousShutdown` → keep `UnexpectedShutdown` (Warning) but add the last-known user
  (`status.UserName`) to the message for attribution.
* Add `SecurityEventKinds.TamperSuspected`.

**Web (prominence).** Reuse the live `SecurityEventRaised` stream:
* Classify kinds `TAMPER_SUSPECTED` and `UNEXPECTED_SHUTDOWN` as **tamper-class**.
* On a tamper-class event: show a toast. On the overview: a banner counting unresolved tamper-class alerts, linking to
  the Security page (which already lists + acknowledges).

### Severity / false-positive policy

Only a **deliberate graceful stop while a user is signed in and the OS is not shutting down** is Critical
`TAMPER_SUSPECTED`. Normal shutdowns are silent. Crashes/kills remain Warning `UnexpectedShutdown` (we cannot reliably
tell a kill from a genuine crash, so we do not cry wolf — but we surface and attribute it).

### Test plan

* Agent: `StopClassifier.Classify` truth table (shutting-down→benign; user present + not shutting down→deliberate+user;
  no user→benign).
* Server: `AGENT_STOPPED` with `InteractiveUser` raises Critical `TAMPER_SUSPECTED`; without it stays `ServiceStopped`;
  benign shutdown does not alert (against real PostgreSQL).
* Web: tamper-class classifier + the unresolved-count selector (vitest).

### What still needs a real elevated PC (not unit-testable here)

The actual SM_SHUTTINGDOWN reading and WTS user lookup during a real service stop, and that the hardened ACL denies a
real admin's stop — covered by the manual install checklist.
