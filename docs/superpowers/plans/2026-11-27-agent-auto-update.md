# Agent Auto-Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the admin publish a new signed agent build from the dashboard and have the ~1500-machine fleet update itself — canary first, auto-rollback on failure — with no per-PC visit.

**Architecture:** The heartbeat already returns version info; we extend it with a per-device **target version** computed by a server-side **rollout state machine**. The admin uploads a **signed package** (verified on upload and again on each PC against the internal-CA publisher). The **watchdog** (SYSTEM) downloads, verifies, installs side-by-side, flips the service `binPath`, health-gates on real collector liveness, and rolls back on failure. A rollout-aware server reclassifies the update restart so it never fires the tamper alarm.

**Tech Stack:** .NET 10 (net10.0 / net10.0-windows), C#, xUnit; ASP.NET Core Minimal APIs, EF Core + PostgreSQL; Next.js 16 / React / vitest; PowerShell build + signing; Authenticode / internal ADCS.

**Spec:** `docs/superpowers/specs/2026-11-27-agent-auto-update-design.md` (read it — every task's rationale and the eight review resolutions live there).

## Global Constraints

- Every new event type is added to **both** `EventTypes.All` **and** `PayloadCatalog.Build()`, or it is silently dropped at ingestion (200 batch, event rejected "unknown eventType").
- Server/agent builds and tests run with `-p:NuGetAudit=false` (pre-existing NU1903 advisory on SignalR.Common; not ours).
- Shared request/response/payload records live in `src/NISS.Monitor.Protocol`; the test project resolves them via `using NISS.Monitor.Protocol;`.
- **Signing = internal ADCS.** Verification is **publisher-identity pinning** (Authenticode chains to the pinned subject/CA), never leaf-thumbprint equality. Rotation uses a dual-trust window.
- **Signed-or-it-doesn't-run.** The agent installs nothing that fails verification; the server stores nothing that fails verification; publish is SUPERADMIN-only and audit-logged.
- **Tamper suppression is server-authoritative** — a device's update restart is benign only when the *server's own* rollout record assigned that device that update. An agent-supplied hint is never sufficient alone.
- Rolled-back devices are **quarantined** from the version they failed; `MinAgentVersion` cannot be set to a version with unresolved rollback failures (explicit override only).
- **Health = ACTIVE heartbeat + all expected collectors healthy + session attached**, sustained over a canary **bake window** (default 24 h, admin-settable). Timing is **anytime, throttled** (per-device jitter + server download concurrency cap); no off-hours scheduler in v1.
- The updater runs as SYSTEM and must keep working against the hardened service ACL (`HardenedServiceSddl`): never weaken the ACL to update; re-apply it after a swap.

## Review Focus

- **Cert rotation / dual-trust:** an update signed by a freshly rotated internal-CA cert that agents have not yet been taught to trust must not brick the channel (agents accept old-or-new pin during the window). Test in Task 2.
- **A device offline for the entire rollout:** it must stay `pending`, be excluded from the canary health-gate denominator, and update when seen — never permanently block "Roll out to all". Test in Task 4 / Task 7.
- **Forged "updating" hint:** a local admin stopping the service with a fake update marker, while the server has *no* assigned in-progress update for that device, must still raise `TAMPER_SUSPECTED`. Test in Task 6.
- **Interrupted download / reboot mid-swap:** nothing is committed until verified + healthy; a crash mid-update leaves the service on the last-good version folder. Test in Task 9.
- **Rollback↔MinVersion loop:** a device that failed version V is never re-targeted to V by a wave or by `MinAgentVersion`. Test in Task 4.

---

## File Structure

- Protocol (`src/NISS.Monitor.Protocol/`): `AgentApi.cs` (+heartbeat fields, `ReleaseManifest`, `UpdateStatusReport`), `DashboardApi.cs` (+update DTOs/requests), `EventTypes.cs` (`SecurityEventKinds.UpdateFailed`, `UpdateRestart` constant), new `Updates/PackageVerification.cs` (cross-platform verify logic).
- Server domain (`server/NISS.Domain/Entities/`): `AgentPackage.cs`, `AgentRollout.cs`, `DeviceUpdateState.cs`.
- Server persistence: `Configurations/UpdateConfigurations.cs` + one migration `AddAgentUpdates`; DbSets on `AppDbContext` + `IAppDbContext`.
- Server app (`server/NISS.Application/Updates/`): `IAgentUpdateService`/`AgentUpdateService`, `IPackageStore` (bytes on disk), `ISignatureChecker` + Windows impl, rollout health logic. Plus edits to the heartbeat handler and the AgentStopped projector.
- Server API (`server/NISS.Api/Features/`): `AgentUpdateEndpoints.cs`.
- Web (`web/niss-monitor-dashboard/`): `features/updates/*`, `app/(app)/updates/page.tsx`, nav entry, `lib/types.ts` DTOs.
- Agent (`src/`): `NISS.Monitor.Core/Updates/` (`UpdateOrchestrator`, `IUpdateHost`, `VersionCompare`), `NISS.Monitor.Infrastructure/Updates/` (`WindowsUpdateHost`, `WinAuthenticodeChecker`), watchdog wiring in `NISS.Monitor.Watchdog/`.
- Build/signing (`deployment/scripts/`): `Build-Agent.ps1`, `Sign-Agent.ps1`.

---

## Task 1: Protocol & contracts for the update channel

**Files:**
- Modify: `src/NISS.Monitor.Protocol/AgentApi.cs`, `src/NISS.Monitor.Protocol/DashboardApi.cs`, `src/NISS.Monitor.Protocol/EventTypes.cs`
- Test: `server/NISS.Api.Tests/UnitTests.cs` (or a new `UpdateContractTests.cs`)

**Interfaces:**
- Produces:
  - `HeartbeatResponse` gains trailing `string? TargetAgentVersion = null, string? UpdateUrl = null` (optional, backward-safe).
  - `ReleaseManifest(string Version, string? MinOs, IReadOnlyList<ManifestFile> Files, bool WatchdogChanged, string? Notes)`; `ManifestFile(string Path, string Sha256)`.
  - `UpdateStatusReport(string Version, string Phase, string? Error, int Attempt)` — phases: `downloading|verifying|installing|health_check|updated|failed|rolled_back`.
  - `AgentPackageDto`, `AgentRolloutDto(string Id, string Version, string State, DateTimeOffset CreatedAtUtc, int Canary, int Updated, int Failed, int Pending)`, `DeviceUpdateStateDto(string DeviceId, string? AssignedVersion, string Phase, string? LastError, string? FailedVersion, DateTimeOffset? UpdatedAtUtc)`, `CreateRolloutRequest(string Version, string? Notes)`, `CanaryRequest(IReadOnlyList<string> DeviceIds)`.
  - `SecurityEventKinds.UpdateFailed = "UPDATE_FAILED"`; `StopReasons.UpdateRestart = "UPDATE_RESTART"` constant.

- [ ] **Step 1: Write the failing test** — `ReleaseManifest round-trips through ProtocolJson` and `HeartbeatResponse with target version deserializes from a payload that omits the new fields` (backward-compat).
- [ ] **Step 2: Run it, watch it fail** (types undefined). Run: `dotnet test server/NISS.Api.Tests --filter UpdateContract -p:NuGetAudit=false`. Expected: FAIL (compile).
- [ ] **Step 3: Add the records/constants** listed in Interfaces.
- [ ] **Step 4: Run, watch it pass.**
- [ ] **Step 5: Commit** `feat(protocol): contracts for agent auto-update channel`.

## Task 2: Package verification (hash + version + publisher pin)

**Files:**
- Create: `src/NISS.Monitor.Protocol/Updates/PackageVerification.cs`, `.../Updates/ISignatureChecker.cs`
- Test: `server/NISS.Api.Tests/PackageVerificationTests.cs`

**Interfaces:**
- Consumes: `ReleaseManifest`, `ManifestFile` (Task 1).
- Produces:
  - `readonly record struct VerifyResult(bool Ok, string? Error)`.
  - `record PublisherPin(IReadOnlyList<string> AcceptedSubjects)` — dual-trust = more than one accepted subject/CA during rotation.
  - `interface ISignatureChecker { bool IsAuthenticodeSignedBy(byte[] fileBytes, PublisherPin pin); }`
  - `static VerifyResult PackageVerification.Verify(ReleaseManifest m, Func<string,byte[]> readFile, ISignatureChecker sig, PublisherPin pin, string currentVersion)` — checks: version newer than current; every `ManifestFile.Sha256` matches `readFile`; every executable `IsAuthenticodeSignedBy(pin)`.
  - `static bool VersionCompare.IsNewer(string candidate, string current)`.

- [ ] **Step 1: Failing tests** — `Verify passes for a good manifest`; `tampered file (sha mismatch) fails`; `unsigned/wrong-publisher exe fails` (fake `ISignatureChecker`); `older-or-equal version fails`; `dual-trust: a file signed by the second accepted subject passes` (Review Focus: rotation).
- [ ] **Step 2: Run, watch fail.** Run: `dotnet test server/NISS.Api.Tests --filter PackageVerification -p:NuGetAudit=false`.
- [ ] **Step 3: Implement** `PackageVerification.Verify` + `VersionCompare.IsNewer`. `ISignatureChecker` is an interface only here (Windows impl in Task 10 server-side and agent-side).
- [ ] **Step 4: Run, watch pass.**
- [ ] **Step 5: Commit** `feat(updates): package hash/version/publisher-pin verification`.

## Task 3: Server — package upload + storage + verification

**Files:**
- Create: `server/NISS.Domain/Entities/AgentPackage.cs`, `server/NISS.Application/Updates/IPackageStore.cs` + impl in `server/NISS.Infrastructure/Persistence/` (bytes on disk, like screenshots), `server/NISS.Infrastructure/Updates/WinAuthenticodeChecker.cs` (implements `ISignatureChecker` via `X509Certificate.CreateFromSignedFile`/`SignedCms` + pin subjects)
- Modify: `AppDbContext`, `IAppDbContext`, `DependencyInjection.cs`; new migration `AddAgentUpdates` (packages table; rollout/update-state tables added in Task 4 may share the migration if built together — otherwise two migrations)
- Create: `server/NISS.Api/Features/AgentUpdateEndpoints.cs` (just the upload endpoint this task)
- Test: `server/NISS.Api.Tests/AgentUpdateUploadTests.cs`

**Interfaces:**
- Consumes: `PackageVerification`, `ISignatureChecker` (Task 2); `PublisherPin` from config.
- Produces: `AgentPackage` entity (Id, Version unique, RelPath, SizeBytes, Sha256, SignerSubject, UploadedBy, UploadedAt, Notes); `POST /api/v1/agent-packages` (SUPERADMIN, multipart: zip + manifest) → verifies → stores → `AgentPackageDto`.

- [ ] **Step 1: Failing tests** — `uploading a package the (fake) verifier accepts stores it and returns the version`; `a package the verifier rejects → 400, nothing stored`; `non-SUPERADMIN → 403`. Inject a fake `ISignatureChecker` in the test host.
- [ ] **Step 2: Run, watch fail.** Run: `dotnet test server/NISS.Api.Tests --filter AgentUpdateUpload -p:NuGetAudit=false`.
- [ ] **Step 3: Implement** entity + config + migration + `IPackageStore` + endpoint; register in DI. Audit-log the upload.
- [ ] **Step 4: Run, watch pass + run the full suite** (`dotnet test server/NISS.Api.Tests -p:NuGetAudit=false`).
- [ ] **Step 5: Commit** `feat(server): signed agent-package upload + storage`.

## Task 4: Server — rollout state machine + per-device target

**Files:**
- Create: `server/NISS.Domain/Entities/AgentRollout.cs`, `server/NISS.Domain/Entities/DeviceUpdateState.cs`, `server/NISS.Application/Updates/IAgentUpdateService.cs` + `AgentUpdateService.cs`
- Modify: `AppDbContext`/`IAppDbContext`/config/migration; `DependencyInjection.cs`
- Test: `server/NISS.Api.Tests/RolloutTargetingTests.cs`

**Interfaces:**
- Consumes: `AgentPackage` (Task 3).
- Produces `IAgentUpdateService`:
  - `Task<AgentRollout> CreateRolloutAsync(string version, string? notes, string actor, CancellationToken ct)` (state `Draft`).
  - `Task SetCanaryAsync(Guid rolloutId, IReadOnlyList<Guid> deviceIds, string actor, CancellationToken ct)` (state `Canary`).
  - `Task<string?> TargetVersionForAsync(Device device, string currentVersion, CancellationToken ct)` — returns the version this device should move to, applying: wave membership (canary set, or all when `RolledOut`), the `MinAgentVersion` floor, and the **quarantine** (never the device's recorded `FailedVersion`). Null = stay put.
  - `Task<bool> HasAssignedInProgressUpdateAsync(Guid deviceId, CancellationToken ct)` (for Task 6).
  - `AgentRollout` fields: Version, State (`Draft|Canary|RolledOut|Done|Paused|RolledBack`), CanaryDeviceIds, BakeHours, Threshold, timestamps. `DeviceUpdateState`: DeviceId unique, AssignedVersion, Phase, LastError, FailedVersion, Attempts, UpdatedAt.

- [ ] **Step 1: Failing tests** — `canary device gets the target, non-canary does not`; `after RolledOut, all devices get the target`; `a device whose FailedVersion == target is NOT targeted` (Review Focus: loop); `MinAgentVersion floors a below-min device even outside the wave`; `MinAgentVersion cannot be set to a version with unresolved failures`.
- [ ] **Step 2: Run, watch fail.** Run: `dotnet test server/NISS.Api.Tests --filter RolloutTargeting -p:NuGetAudit=false`.
- [ ] **Step 3: Implement** entities + migration + service.
- [ ] **Step 4: Run, watch pass + full suite.**
- [ ] **Step 5: Commit** `feat(server): rollout state machine + per-device update targeting`.

## Task 5: Server — heartbeat target + agent download + status endpoints

**Files:**
- Modify: the heartbeat handler (`server/NISS.Application/Devices/AgentService.cs` or the heartbeat endpoint) to set `TargetAgentVersion`/`UpdateUrl`; `AgentUpdateEndpoints.cs` (download + status)
- Test: `server/NISS.Api.Tests/AgentUpdateChannelTests.cs`

**Interfaces:**
- Consumes: `IAgentUpdateService.TargetVersionForAsync` (Task 4), `IPackageStore` (Task 3).
- Produces: heartbeat response carries `TargetAgentVersion`/`UpdateUrl`; `GET /api/v1/agent/update/{version}` (device-auth, 403 unless `version` is the device's assigned target, streams zip+manifest); `POST /api/v1/agent/update/status` (device-auth; updates `DeviceUpdateState`; a `rolled_back`/`failed` report raises `SecurityEventKinds.UpdateFailed`, Warning, recording `FailedVersion`).

- [ ] **Step 1: Failing tests** — `an assigned device's heartbeat returns its target version + url`; `an up-to-date device gets null target`; `downloading a version not assigned to the device → 403`; `a rolled_back status report sets FailedVersion and raises UPDATE_FAILED`.
- [ ] **Step 2: Run, watch fail.** Run: `dotnet test server/NISS.Api.Tests --filter AgentUpdateChannel -p:NuGetAudit=false`.
- [ ] **Step 3: Implement.** The download endpoint enforces a **server-side concurrency cap** (configurable, e.g. N simultaneous package streams) and returns `503 Retry-After` past it, so a fleet-wide target flip can't saturate the box that also serves live monitoring (Review Focus / resolution 4). Per-device jitter is the agent's side (Task 10).
- [ ] **Step 4: Run, watch pass + full suite.**
- [ ] **Step 5: Commit** `feat(server): heartbeat target + agent update download/status endpoints`.

## Task 6: Server — tamper-alert suppression during updates

**Files:**
- Modify: the AgentStopped projector (`server/NISS.Application/Ingestion/Projectors/TelemetryProjectors.cs`, the `AGENT_STOPPED`→`TAMPER_SUSPECTED` path)
- Test: `server/NISS.Api.Tests/TamperAlertTests.cs` (extend)

**Interfaces:**
- Consumes: `IAgentUpdateService.HasAssignedInProgressUpdateAsync` (Task 4), `AgentPayload.InteractiveUser`.
- Produces: when a deliberate stop arrives AND the server has an assigned in-progress update for that device, raise `ServiceStopped` with reason `UpdateRestart` instead of `TAMPER_SUSPECTED`; otherwise the existing tamper behavior is unchanged.

- [ ] **Step 1: Failing tests** — `a stop while the device has an assigned in-progress update does NOT raise TAMPER_SUSPECTED`; `a stop with an InteractiveUser and NO assigned update STILL raises Critical TAMPER_SUSPECTED` (Review Focus: forged hint); `a benign shutdown stays ServiceStopped`.
- [ ] **Step 2: Run, watch fail.** Run: `dotnet test server/NISS.Api.Tests --filter TamperAlert -p:NuGetAudit=false`.
- [ ] **Step 3: Implement** the projector branch (server-authoritative check only).
- [ ] **Step 4: Run, watch pass + full suite.**
- [ ] **Step 5: Commit** `fix(server): suppress tamper alert for server-assigned update restarts`.

## Task 7: Server — rollout admin endpoints + health gate

**Files:**
- Modify: `AgentUpdateEndpoints.cs`, `AgentUpdateService.cs`
- Test: `server/NISS.Api.Tests/RolloutLifecycleTests.cs`

**Interfaces:**
- Consumes: `AgentStatus` collector liveness, `DeviceUpdateState`.
- Produces: `POST /agent-rollouts` (create), `POST /agent-rollouts/{id}/canary`, `POST /agent-rollouts/{id}/rollout` (gated), `.../pause`, `.../rollback`; `GET /agent-rollouts/{id}` → `AgentRolloutDto` with counts; `Task<RolloutHealth> CanaryHealthAsync(Guid id, ct)` = updated/failed/pending over devices that **attempted**, requiring collectors healthy + bake time elapsed. Offline/never-seen devices are `pending` and excluded from the gate denominator.

- [ ] **Step 1: Failing tests** — `rollout is blocked until canary healthy AND baked`; `an offline canary device does not block the gate` (Review Focus: offline); `rollback transitions state and quarantines failed devices`; `all admin endpoints require ADMIN/SUPERADMIN and are audited`.
- [ ] **Step 2: Run, watch fail.** Run: `dotnet test server/NISS.Api.Tests --filter RolloutLifecycle -p:NuGetAudit=false`.
- [ ] **Step 3: Implement** transitions + health gate (bake window via the test clock).
- [ ] **Step 4: Run, watch pass + full suite.**
- [ ] **Step 5: Commit** `feat(server): rollout lifecycle endpoints + canary health gate`.

## Task 8: Dashboard — Agent Updates page

**Files:**
- Create: `web/.../src/features/updates/*`, `web/.../src/app/(app)/updates/page.tsx`
- Modify: `web/.../src/components/layout/nav.ts` (Administration group), `web/.../src/lib/types.ts`
- Test: `web/.../src/features/updates/__tests__/*.test.ts`

**Interfaces:**
- Consumes: the Task 3/5/7 endpoints + DTOs.
- Produces: upload control (SUPERADMIN), rollout create, canary device picker, progress bars (canary + fleet), **Roll out to all** button disabled until `CanaryHealthAsync` passes, pause/rollback, per-device status table.

- [ ] **Step 1: Failing tests (vitest)** — `the roll-out button is disabled until canary health passes`; `progress splits updated/failed/pending`.
- [ ] **Step 2: Run, watch fail.** Run: `npm --prefix web/niss-monitor-dashboard test`.
- [ ] **Step 3: Implement** page + types + nav; typecheck clean.
- [ ] **Step 4: Run, watch pass** + `npm --prefix web/niss-monitor-dashboard run typecheck` + production build.
- [ ] **Step 5: Commit** `feat(web): Agent Updates rollout dashboard`.

## Task 9: Agent — update orchestrator (pure state machine)

**Files:**
- Create: `src/NISS.Monitor.Core/Updates/UpdateOrchestrator.cs`, `.../Updates/IUpdateHost.cs`
- Test: `src/NISS.Monitor.Tests/UpdateOrchestratorTests.cs`

**Interfaces:**
- Consumes: `PackageVerification` (Task 2), `ReleaseManifest`, `UpdateStatusReport`.
- Produces:
  - `interface IUpdateHost { Task<byte[]> DownloadAsync(string url, CancellationToken ct); Task InstallSideBySideAsync(string version, …); Task SwitchToAsync(string version); Task<bool> HealthyAsync(string version, TimeSpan window, CancellationToken ct); Task RollbackAsync(string previousVersion); Task ReportAsync(UpdateStatusReport r); }`
  - `UpdateOrchestrator(IUpdateHost host, ISignatureChecker sig, PublisherPin pin)` with `Task RunAsync(string targetVersion, string targetUrl, string currentVersion, CancellationToken ct)`: download → `PackageVerification.Verify` → install side-by-side → switch → `HealthyAsync` → on false `RollbackAsync` + report `rolled_back`; verify-failure aborts **before** install.

- [ ] **Step 1: Failing tests** (fake `IUpdateHost`) — `happy path installs, switches, reports updated`; `verify failure aborts without InstallSideBySide` (Review Focus: nothing committed until verified); `unhealthy new version triggers Rollback + rolled_back report`; `download of a partial/short package fails verification, no install`.
- [ ] **Step 2: Run, watch fail.** Run: `dotnet test src/NISS.Monitor.Tests --filter UpdateOrchestrator`.
- [ ] **Step 3: Implement** the orchestrator.
- [ ] **Step 4: Run, watch pass + agent suite** (`dotnet test src/NISS.Monitor.Tests`).
- [ ] **Step 5: Commit** `feat(agent): update orchestrator state machine`.

## Task 10: Agent — Windows update host + watchdog wiring

**Files:**
- Create: `src/NISS.Monitor.Infrastructure/Updates/WindowsUpdateHost.cs`, `.../Updates/WinAuthenticodeChecker.cs`
- Modify: `src/NISS.Monitor.Watchdog/*` (own device-auth channel, poll target, run orchestrator, report status, session relaunch, mutual repair), `src/NISS.Monitor.Infrastructure/Setup/AgentSetup.cs` (versioned folder layout + `binPath` switch helper), re-apply `HardenedServiceSddl` after a swap
- Test: `src/NISS.Monitor.Tests/WindowsUpdateHostTests.cs` (the testable pieces) + **manual verification checklist**

**Interfaces:**
- Consumes: `IUpdateHost` (Task 9), `UpdateOrchestrator`, `AgentSetup` hardening.
- Produces: `WindowsUpdateHost` implementing `IUpdateHost` (download over TLS, extract to `…\Agent\app\<version>\`, `sc config binPath=`, stop/start, relaunch `Session.exe` from the new version, retain previous for rollback); `WinAuthenticodeChecker : ISignatureChecker` (publisher-identity pin). Watchdog gains an independent authenticated update channel so a wedged service is recoverable.

- [ ] **Step 1: Failing tests** — `version-folder path + binPath command are built correctly`; `rollback repoints to the retained previous version`; `pruning keeps the previous version`. (Pure-logic pieces; real `sc`/file ops behind the host.)
- [ ] **Step 2: Run, watch fail.** Run: `dotnet test src/NISS.Monitor.Tests --filter WindowsUpdateHost`.
- [ ] **Step 3: Implement** the host + watchdog wiring + session relaunch + ACL re-apply. Before downloading, the watchdog waits a **randomized per-device jitter** and honours the server's `503 Retry-After`, so the fleet's ~40 MB pulls spread out instead of stampeding (resolution 4).
- [ ] **Step 4: Run tests; then the MANUAL checklist on a real PC** — install vN, publish vN+1 to that device, confirm: swap + restart, health gate passes, no tamper alert raised, rollback on a deliberately-broken vN+1, session process ends up on the new version, hardened ACL still denies an admin stop. Record results in the ledger.
- [ ] **Step 5: Commit** `feat(agent): Windows side-by-side update host + watchdog updater`.

## Task 11: Build + signing pipeline

**Files:**
- Modify: `deployment/scripts/Build-Agent.ps1` (emit the package layout + `release.json`), `deployment/scripts/Sign-Agent.ps1` (Authenticode-sign exes with the ADCS cert + sign `release.json`)
- Create: `docs/agent-updates-operations.md` (how to publish a release; cert/ADCS prerequisites; off-domain-laptop CA-root note)

**Interfaces:**
- Consumes: `ReleaseManifest` shape (Task 1).
- Produces: a signed package (`<version>.zip` + signed `release.json`) that Task 3 accepts and Task 10 verifies.

- [ ] **Step 1:** Extend the build to produce the manifest with per-file SHA-256 and `WatchdogChanged`.
- [ ] **Step 2:** Extend signing to Authenticode-sign each exe + sign the manifest with the ADCS cert; verify with `Get-AuthenticodeSignature`.
- [ ] **Step 3:** Produce a package and run it end-to-end through the Task 3 upload on the dev server; confirm acceptance.
- [ ] **Step 4:** Write `docs/agent-updates-operations.md` (publish steps, ADCS prerequisites, dual-trust rotation, off-domain CA-root caveat).
- [ ] **Step 5: Commit** `feat(build): signed update-package + manifest pipeline + ops doc`.

---

## Notes for the executor

- Bootstrap: the current fleet can't auto-update, so Tasks 1–11 ship a new agent that must be rolled out **once** by the existing installer; after that, updates are in-band.
- Tasks 1–8 are fully TDD-able on this machine (server tests need Postgres on 5433; `-p:NuGetAudit=false`). Tasks 9–11 unit-test their logic; the real Windows swap is the manual checklist in Task 10.
- Do not weaken `HardenedServiceSddl` to perform updates — the SYSTEM watchdog already has the rights; re-apply the ACL after any `binPath`/service change.
