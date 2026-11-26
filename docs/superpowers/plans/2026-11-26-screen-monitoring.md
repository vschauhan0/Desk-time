# Screen Monitoring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add silent periodic screenshot capture with a per-PC dashboard timeline, plus on-demand view-only live screen streaming with a click-through "being viewed" indicator on the employee's screen.

**Architecture:** The desktop-capable session helper captures the screen (GDI BitBlt → JPEG) and ships bytes to the background service over a new binary named pipe. The service uploads periodic stills via a new REST endpoint (files on disk + Postgres metadata) and, while a SUPERADMIN watches, relays live JPEG frames up a persistent WebSocket; the server fans frames to the browser over a SignalR hub. Capture is policy-gated and ships disabled.

**Tech Stack:** .NET 10 (agent + ASP.NET Core API), System.IO.Pipes, System.Drawing/GDI (BitBlt), WebSockets, SignalR, EF Core + PostgreSQL, Next.js/React dashboard.

**Spec:** `docs/superpowers/specs/2026-11-26-screen-monitoring-design.md`

## Global Constraints

- Both timeline and live are **SUPERADMIN-only** (`AuthDefaults.SuperAdminPolicy`). Non-superadmins never see the Screens UI or any screen bytes.
- Capture **ships disabled**: `ConfigurationPolicy.ScreenCaptureEnabled` default `false`; nothing captures until policy turns it on.
- **Periodic capture is silent; live view shows the indicator.** The indicator appears on live start and is gone on stop/disconnect/timeout.
- **Live frames are never written to disk.** Only periodic stills are stored.
- Periodic capture runs only when the session is **interactive, unlocked, and active**; skip when locked/idle.
- Live session hard cap **30 min** (`ScreenLive:MaxSessionMinutes`); live fps cap **8** (`ScreenLive:MaxFps`); default periodic interval **60 s**, JPEG quality ~60, max width ~1600; live JPEG quality ~50, max width ~1280.
- Stored images retained **14 days** (`ScreenshotStorage:RetentionDays`), deleted as files + rows.
- Session helper holds **no server credentials**; all server auth stays in the service.
- Existing pipe (line-delimited JSON, 128 KB cap) stays untouched; image bytes use a separate binary path.

## Review Focus

- **A watcher disconnects without "stop" (browser tab closed/crash):** the agent must stop live capture and remove the indicator (socket close / timeout fallback). Test in Task 10.
- **Two SUPERADMINs watch the same PC, or one watches two PCs:** frames must reach exactly the right viewers and no others; no cross-device leakage. Test in Task 9.
- **Upload backlog while offline / slow link:** periodic images must drop oldest-first under a bounded cap, never block events or grow unbounded. Test in Task 6.
- **Policy disabled mid-session / capture flag off:** capture and live must refuse to run; live-start returns a clear error. Test in Tasks 7 and 9.
- **Locked workstation / no interactive session:** periodic capture skips; live-start is rejected with "no interactive session". Test in Tasks 5 and 9.

---

## Task 1: Protocol & policy contracts (shared)

**Files:**
- Modify: `src/NISS.Monitor.Protocol/` (add message types) — `ScreenFrameHeader`, `LiveControlMessage`, `ScreenPipeKind`
- Modify: `src/NISS.Monitor.Core/Configuration/AgentSettings.cs` (screenshot settings block)
- Modify: `server/NISS.Domain/Entities/Admin.cs` (`ConfigurationPolicy` capture fields)
- Test: `src/NISS.Monitor.Tests/ProtocolTests.cs`

**Interfaces:**
- Produces: `ScreenFrameHeader(ScreenPipeKind Kind, int Monitor, int Width, int Height, long CapturedAtUtcTicks, int ByteLength)`; `enum ScreenPipeKind { Periodic, Live }`; `LiveControlMessage(string Action, Guid LiveSessionId, int Monitor, int Fps, int Quality)` with Action ∈ {start, stop, switch}; policy fields `ScreenCaptureEnabled:bool`, `ScreenCaptureIntervalSeconds:int`, `ScreenCaptureQuality:int`, `ScreenCaptureMaxWidth:int`, `LiveViewEnabled:bool`.

- [ ] **Step 1:** Write failing test `ScreenFrameHeader_round_trips_through_protocol_json` (serialize/deserialize equality) and `ConfigurationPolicy_defaults_have_capture_disabled`.
- [ ] **Step 2:** Run; expect FAIL (types undefined).
- [ ] **Step 3:** Add the records/enum to Protocol, settings block to `AgentSettings`, and the five columns to `ConfigurationPolicy` (defaults: disabled, 60, 60, 1600, true).
- [ ] **Step 4:** Run tests; expect PASS.
- [ ] **Step 5:** Commit `feat(protocol): screen capture + live control contracts`.

---

## Task 2: Screenshot storage + entity + migration (server)

**Files:**
- Create: `server/NISS.Domain/Entities/Screenshot.cs`
- Modify: `server/NISS.Infrastructure/Persistence/` (DbContext set + entity config)
- Create: EF migration `AddScreenshots` (screenshots table + policy columns from Task 1)
- Create: `server/NISS.Infrastructure/Storage/FileScreenshotStore.cs` implementing `IScreenshotStore`
- Test: `server/NISS.Api.Tests/ScreenshotStoreTests.cs`

**Interfaces:**
- Produces: `Screenshot { Guid Id; Guid DeviceId; Guid? EmployeeId; string SessionUser; DateTimeOffset CapturedAtUtc; int MonitorIndex; int Width; int Height; int ByteSize; string RelPath; DateTimeOffset CreatedAt; }`; `IScreenshotStore.SaveAsync(Guid deviceId, ScreenFrameHeader, byte[] jpeg, ct) -> Screenshot`, `OpenRead(Screenshot) -> Stream`, `DeleteAsync(IEnumerable<Screenshot>, ct)`.
- Consumes: Task 1 policy columns.

- [ ] **Step 1:** Write failing test `SaveAsync_writes_file_and_row` and `DeleteAsync_removes_file_and_row` (against the test PostgreSQL + temp dir).
- [ ] **Step 2:** Run; expect FAIL.
- [ ] **Step 3:** Add entity + EF config, generate migration (`dotnet ef migrations add AddScreenshots`), implement `FileScreenshotStore` writing `{Root}/{deviceCode}/{yyyy-MM-dd}/{ticks}-{monitor}.jpg`.
- [ ] **Step 4:** Run tests; expect PASS. Also run `dotnet ef database update` path via the server test harness (migrations apply on startup).
- [ ] **Step 5:** Commit `feat(server): screenshots table, file store, migration`.

---

## Task 3: Screenshot ingestion endpoint (server, device-authenticated)

**Files:**
- Create: `server/NISS.Application/Screenshots/ScreenshotIngestionService.cs` (`IScreenshotIngestionService`)
- Modify: `server/NISS.Api/Features/` add `ScreenshotEndpoints.cs`
- Test: `server/NISS.Api.Tests/ScreenshotIngestionTests.cs`

**Interfaces:**
- Produces: `POST /api/v1/devices/screenshots` (DevicePolicy, RateLimiting.Ingest) — body = JPEG bytes; headers/query carry monitor, width, height, capturedAt, frameId; idempotent on `frameId`. `IScreenshotIngestionService.IngestAsync(Guid deviceId, ScreenFrameHeader, string frameId, byte[] jpeg, ct)`.
- Consumes: Task 2 `IScreenshotStore`.

- [ ] **Step 1:** Write failing tests `Ingest_persists_screenshot`, `Ingest_is_idempotent_on_frameId`, `Ingest_rejects_when_not_device_authenticated`.
- [ ] **Step 2:** Run; expect FAIL.
- [ ] **Step 3:** Implement service + endpoint; map `ScreenFrameHeader` from request; dedupe on `frameId`.
- [ ] **Step 4:** Run tests; expect PASS.
- [ ] **Step 5:** Commit `feat(server): screenshot ingestion endpoint`.

---

## Task 4: Screenshot read APIs + audit (server, SUPERADMIN)

**Files:**
- Modify: `server/NISS.Application/Screenshots/ScreenshotQueryService.cs`
- Modify: `server/NISS.Api/Features/ScreenshotEndpoints.cs`
- Test: `server/NISS.Api.Tests/ScreenshotQueryTests.cs`

**Interfaces:**
- Produces: `GET /api/v1/devices/{deviceId}/screenshots?from&to&page` → page of metadata (cap page size 200); `GET /api/v1/devices/{deviceId}/screenshots/{id}/image` → streamed JPEG. Both `SuperAdminPolicy`; the image GET writes an audit entry via `IAuditService`.
- Consumes: Task 2 entity/store.

- [ ] **Step 1:** Write failing tests `List_returns_metadata_in_range`, `Image_streams_bytes_and_audits`, `Viewer_role_is_forbidden`.
- [ ] **Step 2:** Run; expect FAIL.
- [ ] **Step 3:** Implement query service + two endpoints + audit call.
- [ ] **Step 4:** Run tests; expect PASS.
- [ ] **Step 5:** Commit `feat(server): screenshot timeline read APIs + audit`.

---

## Task 5: GDI capture engine + periodic policy gating (agent session)

**Files:**
- Create: `src/NISS.Monitor.Infrastructure/Capture/GdiScreenCaptureService.cs` (replaces use of `DisabledScreenCaptureService`)
- Modify: `src/NISS.Monitor.Infrastructure/ServiceCollectionExtensions.cs` (register real service on Windows)
- Modify: `src/NISS.Monitor.Core/Abstractions/Collectors.cs` (add `CaptureMonitorAsync(int monitor, int maxWidth, int quality)` → `byte[]` jpeg + enumerate monitors)
- Test: `src/NISS.Monitor.Tests/ScreenCaptureTests.cs`

**Interfaces:**
- Produces: `IScreenCaptureService.CaptureMonitorAsync(int monitorIndex, int maxWidth, int quality, ct) -> ScreenCaptureFrame(byte[] Jpeg, int Width, int Height)`; `EnumerateMonitors() -> IReadOnlyList<MonitorInfo>`; existing `CaptureAsync` returns an actual frame when enabled.
- Consumes: Task 1 settings.

- [ ] **Step 1:** Write failing tests: `Enumerate_returns_at_least_primary` and `Capture_produces_jpeg_within_maxwidth` — **guarded to skip when no interactive desktop** (headless CI), asserting the guard path returns `Disabled`.
- [ ] **Step 2:** Run; expect FAIL.
- [ ] **Step 3:** Implement GDI BitBlt of the monitor bounds, downscale to maxWidth, JPEG encode at quality; monitor enumeration via `Screen.AllScreens`/Win32.
- [ ] **Step 4:** Run tests; expect PASS (real capture asserted only when a desktop is present).
- [ ] **Step 5:** Commit `feat(agent): GDI screen capture engine`.

---

## Task 6: Binary screen pipe + periodic uploader (agent)

**Files:**
- Create: `src/NISS.Monitor.Infrastructure/Ipc/ScreenPipe.cs` (`ScreenPipeServer` service-side, `ScreenPipeClient` session-side; length-prefixed binary frames)
- Create: `src/NISS.Monitor.Infrastructure/Live/ScreenshotUploader.cs` (bounded queue, drop-oldest, POST to Task 3 endpoint)
- Modify: `src/NISS.Monitor.Session/Program.cs` (periodic capture loop → screen pipe)
- Modify: `src/NISS.Monitor.Service/Workers/PipelineWorkers.cs` (screen pipe server → uploader)
- Test: `src/NISS.Monitor.Tests/ScreenPipeTests.cs`, `ScreenshotUploaderTests.cs`

**Interfaces:**
- Produces: `ScreenPipeClient.SendFrameAsync(ScreenFrameHeader, byte[])`; `ScreenPipeServer` raises frames to a handler; `ScreenshotUploader.Enqueue(ScreenFrameHeader, byte[])` with cap N (default 32) drop-oldest.
- Consumes: Task 1 header, Task 5 frames, Task 3 endpoint.

- [ ] **Step 1:** Write failing tests `ScreenPipe_round_trips_binary_frame`, `Uploader_drops_oldest_when_full`, `Uploader_posts_frame_to_server` (fake handler).
- [ ] **Step 2:** Run; expect FAIL.
- [ ] **Step 3:** Implement second named pipe with length-prefix framing + session-identity check (mirror existing pipe verification); bounded channel uploader; wire session periodic loop (gated: enabled + active + unlocked) and service bridge.
- [ ] **Step 4:** Run tests; expect PASS.
- [ ] **Step 5:** Commit `feat(agent): binary screen pipe + periodic uploader`.

---

## Task 7: Policy surfacing + capture gating end-to-end (agent + server)

**Files:**
- Modify: `server/NISS.Application/Devices/AgentService.cs` (include capture fields in `AgentConfigResponse`)
- Modify: `src/NISS.Monitor.Infrastructure/...` policy consumer (hot-apply capture settings)
- Test: `server/NISS.Api.Tests/AgentConfigTests.cs`, `src/NISS.Monitor.Tests/CaptureGatingTests.cs`

**Interfaces:**
- Consumes: Task 1 policy columns; Task 6 uploader/loop.

- [ ] **Step 1:** Write failing tests `Config_includes_capture_settings`, `Capture_loop_noops_when_disabled`, `Capture_loop_noops_when_locked`.
- [ ] **Step 2:** Run; expect FAIL.
- [ ] **Step 3:** Surface policy fields; gate the capture loop on enabled + presence state.
- [ ] **Step 4:** Run tests; expect PASS.
- [ ] **Step 5:** Commit `feat: policy-gated screen capture`.

---

## Task 8: Live control socket (service ↔ server) + SignalR relay hub (server)

**Files:**
- Create: `server/NISS.Api/Realtime/ScreenLiveHub.cs` (or group on existing hub) + relay registry `ILiveRelay`
- Create: `server/NISS.Api/Features/LiveSocketEndpoint.cs` (`/api/v1/agent/live-socket`, DevicePolicy, WebSocket)
- Create: `src/NISS.Monitor.Infrastructure/Live/LiveSocketClient.cs` (service holds persistent WS, reconnect/backoff)
- Test: `server/NISS.Api.Tests/LiveRelayTests.cs`

**Interfaces:**
- Produces: `ILiveRelay.StartSession(Guid deviceId, Guid liveSessionId)`, `PushFrame(Guid liveSessionId, byte[] jpeg, int monitor)`, `StopSession(...)`; browser SignalR group `live:{liveSessionId}`; agent WS up = binary frames tagged with liveSessionId, down = `LiveControlMessage`.
- Consumes: Task 1 control message.

- [ ] **Step 1:** Write failing tests `Relay_forwards_frame_to_group`, `Relay_isolates_frames_by_session` (two sessions, no cross-leak), `LiveSocket_requires_device_auth`.
- [ ] **Step 2:** Run; expect FAIL.
- [ ] **Step 3:** Implement WS endpoint, relay registry, hub group fan-out with **latest-frame-wins per viewer**; implement agent `LiveSocketClient` (connect, receive control, send frames).
- [ ] **Step 4:** Run tests; expect PASS.
- [ ] **Step 5:** Commit `feat: live frame relay (websocket + SignalR)`.

---

## Task 9: Live view orchestration + authorization + audit (server, SUPERADMIN)

**Files:**
- Create: `server/NISS.Application/Live/LiveViewService.cs` (`ILiveViewService`)
- Modify: `server/NISS.Api/Features/ScreenshotEndpoints.cs` add `live-view/start|stop|switch`
- Test: `server/NISS.Api.Tests/LiveViewServiceTests.cs`

**Interfaces:**
- Produces: `POST /api/v1/devices/{deviceId}/live-view/start` → `{ liveSessionId, hubGroup }` (SuperAdminPolicy); `.../stop`, `.../switch?monitor=`. Rejects when device offline / no interactive session / `LiveViewEnabled` false. Writes audit on start and end (admin, device, monitor, duration). Enforces 30-min cap.
- Consumes: Task 8 relay + agent socket; `ILiveStateService` for presence.

- [ ] **Step 1:** Write failing tests `Start_rejected_when_offline`, `Start_rejected_when_live_disabled`, `Start_sends_start_down_socket_and_audits`, `Stop_sends_stop_and_audits_duration`, `Session_autostops_after_cap`.
- [ ] **Step 2:** Run; expect FAIL.
- [ ] **Step 3:** Implement orchestration, presence checks, audit, timeout.
- [ ] **Step 4:** Run tests; expect PASS.
- [ ] **Step 5:** Commit `feat(server): live-view orchestration + audit`.

---

## Task 10: Live capture + click-through indicator (agent session)

**Files:**
- Create: `src/NISS.Monitor.Infrastructure/Capture/LiveIndicatorOverlay.cs` (layered, click-through, topmost window)
- Modify: `src/NISS.Monitor.Session/Program.cs` (handle live start/stop from service via screen pipe control; capture loop at live fps; show/hide indicator)
- Modify: `src/NISS.Monitor.Service/Workers/PipelineWorkers.cs` (bridge WS control ↔ session screen pipe)
- Test: `src/NISS.Monitor.Tests/LiveIndicatorTests.cs`, `LiveCaptureControlTests.cs`

**Interfaces:**
- Produces: `LiveIndicatorOverlay.Show(int monitor)`, `Hide()` — window styles `WS_EX_LAYERED|WS_EX_TRANSPARENT|WS_EX_NOACTIVATE|WS_EX_TOPMOST`; `LiveCaptureController.Start(LiveControlMessage)`, `Stop()` driving Task 5 capture at live cadence.
- Consumes: Task 1 control, Task 5 capture, Task 8 socket (via service bridge).

- [ ] **Step 1:** Write failing tests `Indicator_show_then_hide_tracks_state` (state machine, no real window in CI), `Live_start_shows_indicator_and_streams`, `Live_stop_or_disconnect_hides_indicator` (disconnect path).
- [ ] **Step 2:** Run; expect FAIL.
- [ ] **Step 3:** Implement overlay (guarded for headless), live capture controller, and the disconnect/timeout → stop+hide fallback.
- [ ] **Step 4:** Run tests; expect PASS.
- [ ] **Step 5:** Commit `feat(agent): live capture + being-viewed indicator`.

---

## Task 11: Dashboard — Screens tab: timeline + live view (web, SUPERADMIN)

**Files:**
- Create: `web/niss-monitor-dashboard/src/components/screens/ScreenTimeline.tsx`, `LiveViewPane.tsx`, `MonitorSwitcher.tsx`
- Modify: device detail page to add a SUPERADMIN-only **Screens** tab
- Modify: API client (`screenshots` list/image, `live-view` start/stop/switch, hub join)
- Test: `web/.../__tests__/screens.test.tsx`

**Interfaces:**
- Consumes: Task 4 read APIs, Task 9 live-view APIs, Task 8 SignalR group.

- [ ] **Step 1:** Write failing tests `Screens_tab_hidden_for_non_superadmin`, `Timeline_renders_thumbnails_and_opens_lightbox`, `Watch_live_disabled_when_offline`.
- [ ] **Step 2:** Run; expect FAIL.
- [ ] **Step 3:** Implement timeline filmstrip + scrubber + lightbox; live pane rendering JPEG frames from the hub with monitor switcher + Stop; gate tab by role.
- [ ] **Step 4:** Run tests; expect PASS; `npm run build`.
- [ ] **Step 5:** Commit `feat(web): screens timeline + live view`.

---

## Task 12: Retention + settings + docs

**Files:**
- Modify: retention worker (delete screenshots older than `RetentionDays`, files + rows)
- Modify: `serversettings.json` / example (`ScreenshotStorage`, `ScreenLive` sections)
- Modify: `README.md` + `docs/monitoring-events.md` / `docs/security.md` (screenshot + live view, privacy note, disclosure assumption)
- Test: `server/NISS.Api.Tests/RetentionTests.cs` (screenshot branch)

- [ ] **Step 1:** Write failing test `Retention_deletes_expired_screenshots_files_and_rows`.
- [ ] **Step 2:** Run; expect FAIL.
- [ ] **Step 3:** Extend retention worker; add settings sections (disabled defaults); document the feature, the SUPERADMIN gate, retention, and the written-disclosure assumption.
- [ ] **Step 4:** Run tests; expect PASS; run full agent + server suites.
- [ ] **Step 5:** Commit `feat: screenshot retention + settings + docs`.

---

## Notes for execution
- Branch off `main` first (do not build on `main`).
- Native capture, the overlay window, and the live socket cannot be fully verified in headless CI or without an elevated multi-PC setup; those tasks assert state machines + guarded paths in tests and are flagged for pilot verification (consistent with the spec's "not verified" posture).
- After all tasks: one fresh whole-branch review.
