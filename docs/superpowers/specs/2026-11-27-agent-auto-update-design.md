# NISS Agent Auto-Update — Design

**Status:** Draft for approval
**Date:** 2026-11-27
**Related:** [tamper-resistance-design](2026-11-26-tamper-resistance-design.md), `docs/code-signing-and-allowlisting.md`

## Intent & why

NISS is deployed to ~1500 employee PCs. Today, shipping any agent change means re-running
`NISSMonitorSetup.exe` on every machine by hand — infeasible at that scale. The admin wants to publish a
new agent version centrally and have the fleet update itself, on a button press, safely.

This is an **agent auto-update system**: a controlled channel that pushes a new, signed agent build to the
fleet and has each machine install it and restart onto it, with a canary stage and automatic rollback.

## Goals

- Admin uploads a new agent build in the dashboard and rolls it out to the fleet without touching any PC.
- **Staged rollout**: a new version goes to an admin-chosen **canary** group first; only after the canary is
  confirmed healthy can it be rolled out to all.
- **Auto-rollback**: any PC where the new version does not come back healthy reverts to the previous version
  on its own, and reports the failure.
- Offline PCs update when they next come online; the fleet converges.
- Full visibility: per-device update status and fleet rollout progress in the dashboard.

## Non-goals

- Delta/binary-diff patching (ship the whole agent build each time; it is a self-contained publish).
- Updating anything but the NISS agent (not Windows, not third-party software).
- A general remote-command/script channel. This pushes **signed NISS builds only** — nothing else.

## Anti-goals (would make this a failure even if it "works")

- An **unsigned or unverified** update path. A fleet-wide "run this executable" channel that does not
  cryptographically verify the publisher is a fleet-wide malware vector. If signature verification is ever
  bypassable, the feature is wrong.
- A **covert** channel. This is transparent and admin-controlled, consistent with the disclosed-monitoring
  charter — not a backdoor. (See the hard boundary in the tamper-resistance design.)
- A rollout that can brick all 1500 machines at once with no canary and no way back.

## Constraints

- **Signed + verified, always.** Packages are signed with the org code-signing cert (already introduced for
  anti-tamper Layer 4). The agent refuses any package not validly signed by the **pinned** publisher, and the
  server refuses to store an unsigned package. Download is over the existing TLS (8443).
- **Windows self-update mechanics.** A running service cannot overwrite its own on-disk image. Updates use
  **side-by-side versioned folders** + service `binPath` reconfiguration, driven by the peer service, so no
  component replaces its own running image.
- **Must coexist with the tamper hardening.** The updater runs as **SYSTEM** (the watchdog service), which
  keeps full control of the hardened service even though Administrators are denied STOP/DELETE. Consistent
  with `HardenedServiceSddl`.
- **Privilege + audit.** Only SUPERADMIN can upload and publish a release. Every publish/rollout/rollback is
  written to the audit log.

## Approaches considered

1. **Re-run the full `NISSMonitorSetup.exe` in update mode, triggered remotely.** Reuses the installer, but
   the installer is interactive-ish and heavy, and self-replacement/rollback is awkward. Rejected as the
   primary mechanism (the installer stays the first-touch tool).
2. **Signed package + peer-service side-by-side swap (chosen).** Build a signed package of the agent
   binaries; the watchdog installs it beside the current version, flips the service to it, health-checks, and
   rolls back on failure. Clean rollback, no self-replacement deadlock, reuses the existing version/heartbeat
   channel and the existing watchdog.
3. **Third-party management (Intune / WSUS / an MSI auto-updater).** Offloads the mechanics, but adds an
   external dependency the customer may not run, and this fleet is managed by NISS itself on a private LAN.
   Documented as an alternative in the rollout docs, not the build.

## Design

### Control plane (already half-present)

Every `HeartbeatResponse` already carries `MinAgentVersion` and `LatestAgentVersion`. We extend the heartbeat
response with the **target version assigned to *this* device** by the active rollout wave (not a single global
value), plus where to fetch it:

- `TargetAgentVersion` — the version this device should be running (its wave's version), or null.
- `UpdateUrl` — path to download the package + manifest for that version.
- `MinAgentVersion` stays a hard floor: a device below it is targeted regardless of wave (security).

The agent already GETs `/agent/config` when `ConfigVersion` increases; the update check rides the same
heartbeat it already sends — no new polling loop.

### Package & manifest

A release package is a **signed zip** of the published agent binaries (`Windows.RunService.exe`,
`Session.exe`, `NISSAgent.exe`, watchdog, support files) plus a **signed manifest** `release.json`:

```
{ "version": "1.4.0", "minOs": "10.0.17763", "files": [ { "path": "Windows.RunService.exe", "sha256": "…" }, … ],
  "watchdogChanged": false, "notes": "…" }
```

Verification the agent performs before trusting a package (all must pass):
1. The manifest is signed by the **pinned** publisher (thumbprint configured at install).
2. Every file's SHA-256 matches the manifest.
3. Every executable carries a valid **Authenticode** signature chaining to the pinned publisher.
4. `version` > current and `minOs` satisfied.

The server verifies 1–3 on upload and refuses otherwise.

### Server

New entities / state:
- `AgentPackage` — uploaded release: version, file bytes on disk (like screenshots), sha256s, signer
  thumbprint, uploadedBy, uploadedAt, notes.
- `AgentRollout` — one active rollout per channel: packageVersion, state
  (`Draft → Canary → RolledOut → Done`, plus `Paused`/`RolledBack`), canary device set, timestamps.
- Per-device update state (on `AgentStatus` or a new `DeviceUpdateState`): assignedVersion, phase
  (`idle/pending/downloading/verifying/installing/health-check/updated/failed/rolled-back`), lastError,
  attempts, updatedAt.

Endpoints:
- Admin (SUPERADMIN): `POST /agent-packages` (upload, verify signature), `POST /agent-rollouts` (create Draft),
  `POST /agent-rollouts/{id}/canary` (assign canary set, start wave 1), `POST /agent-rollouts/{id}/rollout`
  (all — gated until canary healthy), `POST /agent-rollouts/{id}/pause`, `.../rollback`.
- Agent: `GET /agent/update/{version}` (download package+manifest; device-authenticated, authorized only if
  that version is the device's assigned target), `POST /agent/update/status` (report phase/outcome).

The device's `TargetAgentVersion` is computed from the active rollout: assigned if the device is in the
current wave (canary set, or everyone once rolled out), else its current version. `MinAgentVersion` overrides.

### Agent / watchdog updater (the mechanics)

The **watchdog** (SYSTEM, already a peer service) is the updater for the main service/session/CLI; the **main
service** symmetrically updates the watchdog when `watchdogChanged` — so no component overwrites its own
running image.

Per update (watchdog updating the main service):
1. Heartbeat returns `TargetAgentVersion` > current. Download the package to a staging dir.
2. **Verify** (manifest signature, per-file sha256, Authenticode, pinned publisher). Abort + report on any
   failure — the current version keeps running.
3. Extract to `…\Agent\app\<version>\` (side by side; the current version's folder is untouched).
4. Stop the main service, point it at the new folder (`sc config … binPath=`), start it. Re-apply the
   hardened ACL.
5. **Health gate:** the new version must send a successful heartbeat (200, `DeviceStatus=ACTIVE`) within a
   configurable window (default 10 min) and not crash-loop. On success: report `updated`, prune old versions
   keeping the previous one for rollback.
6. **Rollback:** on failed health gate, repoint `binPath` to the previous version folder, restart, report
   `rolled-back` + `UPDATE_FAILED`. The device stays on the known-good version.

Offline/interrupted updates are safe: nothing is committed until the new version is verified and healthy;
a reboot mid-update leaves the service pointing at the last good folder.

### Staged rollout + auto-rollback (end to end)

1. SUPERADMIN uploads build 1.4.0 → server verifies signature → Release (Draft).
2. Admin selects a canary group (devices / a department) → **Publish to canary**. Only those devices get
   `TargetAgentVersion=1.4.0`; they update and report status; the dashboard shows the canary wave.
3. Server surfaces canary health (updated vs. failed). The **Roll out to all** button is disabled until the
   canary is healthy (configurable threshold, e.g. ≥95% updated, 0 unresolved failures).
4. Admin clicks **Roll out to all** → every device's target becomes 1.4.0; the fleet converges as machines
   heartbeat (offline ones when they return).
5. Any device that fails its health gate auto-rolls-back and shows as **failed** (does not block others);
   the admin can pause or roll back the whole rollout.

### Observability

- "Agent Updates" dashboard page: current release, rollout state, canary vs. fleet progress bars, per-device
  status table, failures with error. Reuses the existing agent-version breakdown on the overview.
- Events: existing `AGENT_VERSION_CHANGED` on success; new security event `UPDATE_FAILED` (Warning) on a
  rollback so failures surface in Security & Agent Health.

## Security model (threat: fleet-wide code execution)

- **Mitigation 1 — signature pinning.** Agent installs only packages whose manifest + every exe are signed by
  the pinned org publisher. Compromising the server is not enough to push code; the attacker also needs the
  signing key (kept offline / in an HSM per the signing doc).
- **Mitigation 2 — server-side verification** on upload (defense in depth; a tampered package is rejected
  before it can be assigned).
- **Mitigation 3 — transport + authz.** TLS download; device-authenticated; a device can only fetch its
  assigned target version; SUPERADMIN-only publish; everything audit-logged.
- **Mitigation 4 — staged + reversible.** Canary + auto-rollback bound the blast radius of a bad (but validly
  signed) build.

## Resolutions from design review (2026-11-27)

A fresh-eyes review surfaced eight cross-cutting risks. Resolutions (now binding on the plan):

1. **No tamper-alert storm during updates.** An update stops the service while a user is signed in — the exact
   `TAMPER_SUSPECTED` trigger. The **server is the authority**: when a device's stop+restart correlates with a
   rollout the server itself assigned to that device (restart lands on the assigned target within the update
   window), the projector classifies it `UPDATE_RESTART`, not tamper. Suppression is driven by **server-side
   rollout state**, which a local admin cannot forge; an agent-supplied "updating" hint is only corroborating,
   never sufficient on its own. Malicious stops with no matching server-assigned update still alert.
2. **No rollback↔MinVersion loop.** A rolled-back device records the exact version it failed and is
   **quarantined** from being re-targeted to *that* version by either a wave or `MinAgentVersion`. The admin is
   **blocked** from setting `MinAgentVersion` to a version that has unresolved rollback failures (explicit
   override required). No thrash.
3. **Pin survives cert rotation.** Verification is **publisher-identity pinning** (Authenticode chains to the
   pinned subject + issuer / public key), not leaf-thumbprint equality — routine renewal under the same CA
   keeps working. Key/CA rotation uses a **dual-trust window**: agents learn the new pin via a config update
   signed by the still-valid old cert *before* the switch, so the channel never bricks.
4. **No thundering herd.** "Roll out to all" does not flip everyone at once — devices take the target as they
   heartbeat, with **randomized per-device jitter** and a **server-side download concurrency cap**, so ~40 MB
   pulls spread out and never saturate the box that also serves live monitoring. (Off-hours window: see policy.)
5. **"Healthy" means actually collecting, over time.** The health gate = ACTIVE heartbeat **+ all expected
   collectors healthy** (the agent already sends `CollectorStatus[]` in `AgentStatusRequest`) **+ the session
   process attached**, sustained over a **canary bake window** (default 24 h, admin-settable) — not an
   instantaneous ping. Catches green-but-blind canaries and delayed failures before "Roll out to all" unlocks.
6. **Mutual repair channel.** The watchdog has its **own device-authenticated** server channel (not only
   service IPC), so a wedged main service can be healed by the watchdog and vice-versa. Watchdog updates are
   rare and get extra canary scrutiny. Simultaneous watchdog+service failure on one box is the residual
   desk-visit case, bounded by the canary.
7. **Offline devices don't stall rollouts.** The gate denominator is devices that **attempted** the update in
   the wave window; never-seen/offline devices are **pending**, excluded from the threshold, and update when
   they reappear. Progress shows updated / failed / pending(offline) separately — rollouts reach "Done"
   despite normal laptop absence.
8. **Sessions move to the new version.** On swap, the service **relaunches `Session.exe` from the new version**
   (terminate old, start new) so service↔session versions match; the health gate includes session
   reattachment. No lingering old-version session or IPC mismatch.

## Decisions (owner-confirmed, 2026-11-27)

- **Signing: internal Windows CA (ADCS).** Packages are signed by a cert issued from the org's internal CA,
  which domain-joined PCs already trust. The agent pins to the **internal publisher identity** (subject +
  chain to the internal CA), per resolution 3. **Consideration:** a PC that is *not* domain-joined (a personal
  or remote laptop) will not trust the internal CA by default — such machines must have the CA root installed
  at enrollment, or they cannot verify updates. Flag any off-domain devices at rollout.
- **Timing: anytime, throttled.** No off-hours scheduler in v1. Devices update as they heartbeat, with
  per-device jitter + a server-side download concurrency cap (resolution 4). A swap is a few-second restart.

## What's left to the builder

- Exact package layout + how the publish build produces and signs `release.json` (extend `Sign-Agent.ps1`).
- The precise `sc config binPath` / folder-flip steps and the watchdog↔service mutual-update handshake.
- Canary "healthy" threshold defaults and the health-gate window.
- Dashboard "Agent Updates" page layout.
- Whether per-device update state extends `AgentStatus` or is its own table (lean: its own table).
```
