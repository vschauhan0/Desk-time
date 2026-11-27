# Agent auto-update: operations

How to build, sign, publish and roll out a new agent version, and what to configure first. Design background:
`docs/superpowers/specs/2026-11-27-agent-auto-update-design.md`.

The model in one paragraph: you build a **signed package** (a zip of the agent files plus a `manifest.json`), upload it to
the server (SUPERADMIN), create a **rollout** (canary devices first, then everyone), and each device's **watchdog**
(SYSTEM) downloads it, verifies it, installs it side by side under `app\<version>`, switches the service to it and
health-checks it, rolling back automatically on failure. Nothing runs unless every `.exe`/`.dll` in the package is
Authenticode-signed by a publisher you have pinned, on **both** the server and the device.

## 1. One-time prerequisites

### 1.1 Code-signing certificate (internal CA / ADCS)

* Issue a certificate from your internal ADCS using a template with the **Code Signing** EKU (1.3.6.1.5.5.7.3.3), key
  exportable only if you intend to use a PFX. Prefer a non-exportable key on the build/release machine.
* Install it in `Cert:\CurrentUser\My` (or `LocalMachine\My`) of the **release machine only**. Never copy it to the server
  or to endpoints. `*.pfx` is git-ignored; do not commit one.
* The certificate's **subject** is what you pin. Note it exactly (e.g. `CN=NISS Agent Publisher, O=Example, C=IN`).
  `Sign-Agent.ps1` prints the exact subject string after signing; the server also shows the signer of each uploaded package.
* Every device and the server must **trust the issuing CA root** (see 1.4), otherwise Authenticode verification fails.

### 1.2 Pin the publisher on the server

`server\NISS.Api\serversettings.json` (or an override via config/env):

```json
"AgentUpdate": {
  "Root": "D:\\NISS\\agent-packages",
  "AcceptedPublisherSubjects": [ "CN=NISS Agent Publisher, O=Example, C=IN" ],
  "UpdateRestartWindowMinutes": 30
}
```

The shipped value `CN=REPLACE-WITH-YOUR-CODE-SIGNING-SUBJECT` is a placeholder; **an empty list (or the placeholder)
rejects every upload**. The server accepts a file when the signing certificate's subject (or a chain issuer's subject)
equals ANY listed entry.

### 1.3 Pin the publisher on every agent

`agentsettings.json` in the agent install directory (next to `Windows.RunService.exe`):

```json
{ "Agent": { "Update": {
    "AcceptedPublisherSubjects": [ "CN=NISS Agent Publisher, O=Example, C=IN" ],
    "PollSeconds": 60,
    "DownloadJitterMaxSeconds": 300
} } }
```

* Updates are **disabled (fail closed) on any device with no subject configured**. The installer does not write this
  yet; provision it with your normal config distribution (and keep it when reinstalling: the installer preserves
  `agentsettings.json`).
* The agent matches the **exact leaf subject**, any-of-N. The server and the agent must therefore list the same leaf
  subject. Treat the two lists as one setting.
* `AllowInsecureHttp` exists for dev only; never set it in production (downloads must be TLS).
* **Certificate rotation (dual trust):** add the NEW subject to both lists first (server, then agents) and wait until
  the fleet has it, THEN start signing with the new certificate, THEN remove the old subject later. Never sign with a
  subject agents do not yet trust: those devices would refuse the update and stay on the old version.

### 1.4 Off-domain laptops: trust the CA root yourself

Domain-joined PCs receive the internal CA root through Group Policy. **Laptops that are not domain joined do not**, so
`WinVerifyTrust` cannot build a chain to your internal CA and the device **cannot verify (and therefore will not
install) any update**. For each such device, import the internal root CA into `Cert:\LocalMachine\Root` (the update runs
as SYSTEM, so the user store is not enough), via your MDM/Intune, a script, or the installer step you control. Verify
with `Get-AuthenticodeSignature <a signed agent exe>` as an admin on the device: Status must be `Valid`. A device that
cannot verify simply reports `failed` and keeps running its current version (safe, but it will never update).

## 2. Publish a release, end to end

All commands from the repo root, on the release machine.

1. **Choose the version** `x.y.z` (three parts, strictly greater than the highest uploaded version; the server and agent
   both refuse same-or-lower). Optionally bump `<Version>` in `Directory.Build.props`, or pass `-Version`.
   The build stamps `Version`/`AssemblyVersion`/`FileVersion` with it. This matters: the agent's health gate requires the
   running agent to report **exactly** the manifest version, otherwise every update is rolled back.
2. **Build and sign:**

   ```powershell
   .\deployment\scripts\Build-Agent.ps1 -Version 1.2.0 -Notes "Fix X" `
       -SignThumbprint <thumbprint>        # or -SignSubject 'CN=NISS Agent Publisher' / -SignPfxPath .\x.pfx
   ```

   Order inside the script (it matters, signing changes a file's bytes): publish all four projects into one payload
   folder, check the watchdog dependency closure, **sign every `.exe`/`.dll`**, build the installer, **hash every shipped
   file**, write `release.json`, zip, then sign `release.json` (detached `release.json.p7s`).
   Output in `artifacts\agent\update\`:
   * `1.2.0.zip`: all payload files (forward-slash entry names) plus `manifest.json`.
   * `release.json`: the same manifest standalone (`{ version, minOs?, files:[{path,sha256}], watchdogChanged, notes? }`).
   * `release.json.p7s`: detached CMS signature of the manifest (audit/attestation; the agent trusts the manifest through
     its per-file SHA-256 and the signed binaries, not through the `.p7s`).
   `agentsettings*.json` are deliberately excluded from the package (site config is never overwritten by a release).
3. **Verify the signatures** (already done by the script; repeat independently if you like):

   ```powershell
   Get-ChildItem .\artifacts\agent\payload -Recurse -Include *.exe,*.dll |
       Get-AuthenticodeSignature | Where-Object Status -ne 'Valid'    # must output nothing
   ```
4. **Upload** as SUPERADMIN: dashboard > Agent Updates > Upload, or `POST /api/v1/agent-packages` (multipart field
   `package` = the zip). The server re-verifies everything (version newer, every SHA-256, every executable signed by a
   pinned publisher) and rejects the upload otherwise; a rejection message names the offending file.
5. **Create a rollout** for the version (`POST /api/v1/agent-rollouts`, or the dashboard), then
   **canary**: choose a few devices (`POST .../{id}/canary`). Watch them go downloading > verifying > installing >
   health_check > updated. The canary gate requires `CanaryMinHealthyRatio` (default 0.95) of attempted canary devices
   to be updated and healthy; devices offline longer than `CanaryStatusFreshMinutes` are pending and excluded.
6. **Roll out to all** (`POST .../{id}/rollout`). Devices pick it up over their poll interval with random jitter, and the
   server caps simultaneous downloads (`MaxConcurrentDownloads`, 503 + `Retry-After` beyond it).
7. **Stop or undo:** `pause` halts new assignments; `rollback` re-targets devices to the previous version. A device whose
   update failed health is rolled back locally and quarantined from that version by the server.

The first rollout of an auto-update-capable agent must go out through the **normal installer** once (the current fleet
cannot update itself); after that, updates are in-band. Installer builds are signed the same way (`Build-Agent.ps1`
signs `NISSMonitorSetup.exe` when a certificate is given).

### Testing the pipeline without the real certificate

`Build-Agent.ps1 -SkipTests` with no `-Sign*` parameter produces the full layout and a correct `manifest.json`
UNSIGNED, which the server will (correctly) reject; use it to check the layout. For a signing dry run, create a
self-signed code-signing cert in `CurrentUser\My` and add `-TimestampUrl '' -AllowUntrustedRoot` (**lab only**: it lets
verification accept a chain not trusted on this machine). Never release such a package.

## 3. Behaviour you should know about

### 3.1 `UpdateRestartWindowMinutes` and the bounded residual

During an update the service is stopped on purpose, which would normally raise a tamper alert. To avoid a flood of false
CRITICAL alerts the server suppresses a service-stopped alert for a device **only within `UpdateRestartWindowMinutes`
(default 30) after the server itself served that package to that device** (a server-recorded timestamp, never an
agent-reported value; one window per served version, re-downloads do not extend it).

* **Too short:** a genuinely slow update (slow disk, big package, retries) exceeds the window and raises a tamper alert.
  Fail-safe, but noisy. Raise it if you see this.
* **Too long:** widens the residual below.
* **Bounded residual (accepted):** a device that is genuinely inside a live rollout wave can have its agent stopped
  during its single window and the alert will be suppressed. The window is short, server-timed, once per version and
  vetoed if the version was quarantined; the stop is still recorded in the audit/event stream. It cannot be extended by
  the agent.

### 3.2 Watchdog self-update is deferred (v1)

The watchdog (`Windows.RunWatchdog.exe`, a SYSTEM service) is **not** swapped by an update in v1, and its service
`binPath` is never changed (updates only repoint the main service). Building with `-WatchdogChanged` sets
`watchdogChanged: true` in the manifest, which makes the agent **log a warning, not apply it**. If a release changes the
watchdog (or anything it loads, e.g. the shared Infrastructure assembly's update logic), the new watchdog code only
reaches a device by **reinstalling with the new installer**. Plan watchdog changes as an installer rollout.
The watchdog's dependency closure (Infrastructure, SQLite, System.Drawing, ...) is shipped in the same payload and
`Build-Agent.ps1` fails the build if any of its runtime DLLs are missing.

### 3.3 Failed-version cooldown is in memory

After a failed attempt the watchdog backs off from that target for an hour using an in-memory `_failedAt` map. It is lost
when the watchdog restarts (including a reboot), so a device may retry a bad target sooner after a restart. The server
side quarantine (per device, per failed version) is the durable control; local retries remain bounded by the health gate
and rollback.

### 3.4 Where things live on a device

`<installDir>\app\<version>\` holds each version (current + previous are kept, older are pruned); the service `binPath`
points at the current one; `update-state.json` records Current/Previous/Pending. The service ACL (`HardenedServiceSddl`)
is re-applied after every switch/rollback; updates never weaken it.

## 4. Known gap to resolve before the first production rollout

`agentsettings.json` is intentionally not in the package, but the updated service reads its settings from **its own
folder** (`app\<version>`), not from the install directory. Confirm the update host carries the site's
`agentsettings.json` (server URL, TLS pin) into each new `app\<version>` folder, or that the service reads it from the
install dir. Checklist item 4 below (the new folder must work with the real site config) is the test; if the updated
service loses its config, fix it before rolling out beyond a canary.

## 5. Manual verification checklist (real Windows PC)

Everything touching a live service (`sc`, SCM stop/start, Authenticode, session relaunch, the Run key, the service ACL)
can only be verified on a real PC. Prereqs: signed packages from section 2; `agentsettings.json` in the install dir has
`Agent:Update:AcceptedPublisherSubjects` = the leaf subject of the signing certificate; the internal CA root is trusted
on the PC; the device is enrolled; the server URL is https.

1. Install vN with the normal installer. Note `sc qc NISSMonitorService` BINARY_PATH_NAME (install dir root) and
   `sc sdshow NISSMonitorService`.
2. Publish vN+1 (upload, canary rollout assigning this device).
3. Within about one poll (plus jitter) confirm in `watchdog-*.log`: downloading, verifying, installing, health_check,
   updated; the dashboard shows the device updated.
4. Confirm `sc qc NISSMonitorService` now points at `...\app\<N+1>\Windows.RunService.exe`; `app\<N+1>` exists and
   contains ONLY the manifest files; `update-state.json` shows Current=N+1, Previous=N, Pending=null. Confirm the updated
   service still has the site configuration (server URL/pin), see section 4.
5. Confirm the service is Running, heartbeats arrive reporting version N+1, and `Windows.RunSession.exe` is running from
   `app\<N+1>\` (Task Manager > Open file location; the old session was ended and relaunched within about 10 s); the HKLM
   Run key `NISSMonitorSession` points at the new folder.
6. Confirm NO tamper alert / AGENT_STOPPED / service-stopped security event on the dashboard for the update restart
   (server window), and no UPDATE_FAILED event.
7. Run `sc sdshow NISSMonitorService` again: the SDDL must equal `HardenedServiceSddl`. As a plain (non-SYSTEM) admin run
   `sc stop NISSMonitorService`: it must be denied (Access is denied).
8. Rollback: publish a deliberately broken vN+2 (valid signature, but e.g. a service exe that exits at start). Expect
   health_check then rolled_back; binPath back to `app\<N+1>`; service Running on N+1; `app\<N+2>` removed; SDDL still
   hardened; the server quarantines N+2 for the device.
9. Tamper cases (expect `failed`, nothing installed, no binPath change): a package with an unsigned or other-publisher
   exe; a package with a flipped byte (hash mismatch); a package with a `..\` manifest path; a same-or-lower version.
10. Crash-resume: during `health_check` stop the Watchdog service (as SYSTEM/admin with rights) or reboot; on restart the
    log shows "Resolved a pending update switch" (confirmed) or a `rolled_back` report.
11. Wedged-service path: stop heartbeats (suspend the service process or block it) for more than 10 minutes with a target
    assigned; the watchdog's own probe (`GET /api/v1/agent/update/target`) should pick up the target and update.
12. Prune: after a third successful update only the current and previous `app\<v>` folders remain.
13. Fleet behaviour: with several devices assigned, downloads start at staggered times; force `503 Retry-After` on the
    server and confirm the agent waits and retries.
14. Off-domain laptop (if you have any): without the CA root in `LocalMachine\Root` the update must report `failed`
    (cannot verify); after importing it, the update succeeds.
