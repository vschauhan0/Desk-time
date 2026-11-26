# Screen monitoring: stored timeline + live view — design

Status: DRAFT (awaiting owner approval)
Date: 2026-11-26
Owner decisions captured by: brainstorming session

## Intent and why

The owner (a SUPERADMIN of the NISS monitoring system) wants to see what is on an
employee's screen on company-managed Windows PCs, for work tracking. Two capabilities:

1. **Stored screenshot timeline** — the agent silently captures a screenshot on a
   fixed interval, uploads it, and the dashboard shows a per-PC filmstrip the admin
   can scroll back through. This is the everyday record.
2. **Live view ("like UltraViewer", view-only)** — from the dashboard, a SUPERADMIN
   picks an online PC and watches its screen in near real time (~5–8 fps). No input,
   no control, nothing recorded. **While a live session is active, the employee sees a
   small discreet on-screen indicator** that their screen is being viewed.

**Silent periodic, notified live.** The **periodic stored screenshots are silent** — no
employee-visible indication — and are the continuous tracking record. The **live view
is notified**: a small, click-through corner indicator ("Screen being viewed by
administrator") is shown on the monitor being watched, only for the duration of the
live session. Rationale: continuous tracking is preserved by the silent timeline, so a
discreet live indicator does not defeat tracking while keeping live watching honest.
This still reverses the original project's blanket "no stealth" stance for the periodic
capture, which is the owner's decision as the system owner on company-owned devices.

### Legal/operational note (not a build constraint, recorded for the owner)
Silent monitoring of company devices is normally lawful **when the organization has
disclosed, in writing (employment contract / acceptable-use policy), that company
devices are monitored** — even without a per-session notice. That written disclosure
is assumed to exist; this system does not create it. Jurisdictions differ; this doc
does not constitute legal advice.

## Goals

- Session helper captures the real desktop (replacing the disabled placeholder).
- Periodic stored screenshots, uploaded reliably, viewable as a per-device timeline.
- On-demand, view-only live streaming at ~5–8 fps with sub-second steady-state latency.
- SUPERADMIN-only for both; every live session and stored-image view is audited.
- **Live sessions show the employee a small, click-through corner indicator** for the
  duration of the watch; periodic capture shows nothing.
- Global on/off + tuning (interval, quality, scale) via the existing policy channel;
  **ships disabled by default** until the owner turns it on.
- Retention job deletes stored images (files + rows) older than N days (default 14).
- No employee-visible UI for periodic capture; the **only** employee-visible element is
  the live-view indicator, shown solely while a live session is active.

## Non-goals

- No remote control (mouse/keyboard), file transfer, or clipboard access. View only.
- No recording/export of the **live** stream; live frames are never written to disk.
- No audio, webcam, or microphone.
- No OCR / content analysis of screenshots in this version.
- No capture of the Windows lock screen or UAC secure desktop (not technically
  reachable from a user-session process; see Limitations).
- Not WebRTC/H.264 (rejected approach 3).

## Anti-goals (would make this a failure even if it "works")

- Frames for device X ever reaching an admin not actively authorized to watch X.
- Live frames being persisted anywhere on the server.
- Capture running (CPU/disk/network) when nobody is watching and periodic capture is
  off, or when the policy flag is off.
- A non-SUPERADMIN gaining access to any screen image or live stream.
- The capture path crashing or wedging the session helper (which also carries
  foreground/idle/input telemetry).
- The live indicator **failing to appear, or lingering after the session ends** — the
  employee must see it whenever (and only when) a live watch is actually running.
- The live indicator stealing focus, blocking clicks, or being capturable as a way to
  tell it is a real notice (it must be click-through and always-on-top).

## Constraints (from the existing codebase)

- **Topology:** `NISSMonitor.Service.exe` runs in session 0 and **cannot see the
  desktop**. `NISSMonitor.Session.exe` runs inside each interactive user session and
  **can**. The session helper holds **no server credentials**; it reaches the service
  only over the named pipe `\\.\pipe\NISSMonitor.Session`. All server auth (device
  credentials, TLS cert pinning, enrollment) lives in the **service**.
- **Pipe:** today it is line-delimited JSON capped at 128 KB — unsuitable for image
  bytes. A binary-capable transfer path is required (see Approaches → transport).
- **Agent→server today:** REST push (`/events/batch`, `/metrics/batch`, heartbeat)
  plus **pull** of effective policy from `GET /api/v1/agent/config`
  (`AgentConfigResponse` → `ConfigurationPolicy`). The agent has **no** push channel.
- **Dashboard↔server:** already a live SignalR connection (used for `EventIngested`,
  live presence via `ILiveStateService`).
- Server: ASP.NET Core + SignalR + PostgreSQL + EF Core migrations. Dashboard: Next.js.
- LAN scale (tens of devices), self-signed pinned TLS, SUPERADMIN role already exists
  (`AuthDefaults.SuperAdminPolicy`).

## Approaches considered

The stored-timeline half is the same in every approach. The approaches differ only in
the **live transport**, because the agent has no existing push channel.

- **A. Poll-driven live (cheapest).** Reuse the config-poll channel to toggle a
  "stream now" flag; agent uploads ~1–2 fps as fast POSTs; dashboard polls newest.
  Rejected as the primary: 1–3 s latency and choppy — not the UltraViewer feel asked
  for. Retained as the conceptual fallback if the real-time channel proves troublesome.
- **B. Persistent real-time channel (CHOSEN).** The **service** holds a persistent,
  auto-reconnecting WebSocket to the server (authenticated with the device credentials
  over the pinned TLS endpoint). It carries small control messages down ("start/stop
  live for this session, monitor N") and binary JPEG frames up. The session helper
  captures and ships frames to the service over a binary pipe path; the service relays
  them up the socket; the server fans them out to the watching SUPERADMIN browser(s)
  over the existing SignalR link. Chosen: smooth, instant start (socket already open),
  auth stays in the service. Cost: one new persistent connection per online device and
  a relay with backpressure.
- **C. WebRTC / H.264 video.** Rejected: heavy native dependencies and signaling
  complexity unjustified for LAN-scale screenshot streaming.

## Chosen design (Approach B)

### 1. Capture (session helper — `NISS.Monitor.Session` / Infrastructure)
- Replace `DisabledScreenCaptureService` with a real Windows capture implementation
  of the existing `IScreenCaptureService`. **GDI `BitBlt`** of the target monitor
  (or full virtual desktop for periodic), scaled down, encoded **JPEG**. GDI BitBlt is
  light at a few fps and avoids the DXGI Desktop-Duplication complexity; if it proves
  insufficient, Desktop Duplication is a later swap behind the same interface.
- **Periodic:** when the capture policy is on and the session is **active and
  unlocked**, capture the full virtual desktop every `IntervalSeconds` (default 60),
  JPEG quality ~60, max width ~1600. Skipped while locked/idle (saves disk, avoids
  lock-screen black frames).
- **Live:** on a "start" command, capture the **selected monitor** at the live cadence
  (target 5–8 fps), JPEG quality ~50, max width ~1280, until "stop"/disconnect/timeout.
- **Live indicator:** on "start", the session helper shows a small always-on-top,
  **click-through** (WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE) overlay
  window in a corner of the watched monitor — a red dot + "Screen being viewed by
  administrator". It is removed on "stop"/disconnect/timeout and on monitor switch it
  moves to the new monitor. Tied to the live-session lifecycle so it cannot linger or
  be dismissed by the employee. Shown **only** for live; periodic capture shows nothing.
- All tunables come from policy (`AgentConfigResponse`); hot-applied on config refresh.

### 2. Session → service transport (binary)
- Add a binary framing path for image bytes: either a **second named pipe**
  (`\\.\pipe\NISSMonitor.Screen`, length-prefixed binary frames) or a length-prefixed
  binary mode on the existing pipe. Keep the 128 KB JSON cap for the control/telemetry
  pipe untouched. Frame header carries: kind (periodic|live), monitor index, width,
  height, captured-at, byte length. Builder picks pipe-vs-second-pipe; **second pipe
  recommended** to keep binary and the line-delimited JSON cleanly separated.
- Same session-identity verification the current pipe uses (client session id matches
  hello; client image is the installed session exe).

### 3. Service → server transport
- **Periodic** stored shots: a new authenticated REST endpoint,
  `POST /api/v1/devices/screenshots` (raw JPEG body or multipart + metadata headers),
  device-authenticated, rate-limited like other ingest, idempotent on a client-supplied
  frame id. Buffered through the agent's existing offline-queue discipline (bounded;
  **images dropped oldest-first under pressure**, not retried forever — they are
  lower-value than events). Capture while offline is best-effort.
- **Live** frames: over the **persistent control WebSocket** the service maintains
  (`/api/v1/agent/live-socket`, device-authenticated, pinned TLS). Down: `start {sessionId,
  monitor, fps, quality}`, `stop`, `switch-monitor`. Up: binary JPEG frames tagged with
  the live-session id. Not stored.
- The socket is opened by the service (reconnect w/ backoff). Control for disable/revoke
  can later ride it too; out of scope here (keep `/agent/config` as the source of truth).

### 4. Server
- **Storage:** image **files on disk** under a configured root
  (`ScreenshotStorage:Root`), path `…/{deviceCode}/{yyyy-MM-dd}/{utcTicks}-{monitor}.jpg`;
  **metadata row** in a new `screenshots` table (id, device_id, employee_id,
  session_user, captured_at_utc, monitor_index, width, height, byte_size, rel_path,
  created_at). Images are **not** stored in Postgres (keeps the DB small). EF Core
  migration adds the table.
- **Live relay:** a SignalR hub (or a group on the existing hub),
  `screen-live/{deviceId}/{liveSessionId}`. Flow: SUPERADMIN browser requests a live
  session (`POST /api/v1/devices/{deviceId}/live-view/start`) → server verifies the
  device is online with an interactive session, sends `start` down that device's live
  socket, returns a `liveSessionId` + hub group → browser joins the group → service
  frames are relayed to the group. **Backpressure: latest-frame-wins**, drop stale
  frames per viewer; never buffer unboundedly. `…/live-view/stop`, tab close, or a
  **30-minute** cap ends it and sends `stop` down the socket.
- **Read APIs (SUPERADMIN only):**
  `GET /api/v1/devices/{deviceId}/screenshots?from&to` → metadata page (cap page size),
  `GET /api/v1/devices/{deviceId}/screenshots/{id}/image` → the JPEG (streamed from disk).
- **Retention:** extend the existing retention worker to delete `screenshots` rows and
  their files older than `RetentionDays` (default 14).
- **Policy:** add capture fields to `ConfigurationPolicy` (enabled, interval, quality,
  scale/max-width, live-enabled). Surfaced in `AgentConfigResponse`. **Default off.**

### 5. Dashboard (Next.js)
- Device detail gains a **"Screens"** tab, SUPERADMIN-only (hidden for other roles):
  - **Timeline:** filmstrip/grid of stored thumbnails by time with a date/time scrubber;
    click → full image in a lightbox. Lazy-loaded.
  - **Watch live:** button (enabled only when the PC is online + has an interactive
    session) → opens a view-only live pane rendering incoming JPEG frames via the hub,
    a **monitor switcher** if multiple monitors, and **Stop**. Auto-stops on close/timeout.
- No employee-facing UI anywhere.

### 6. Access control & audit
- Both timeline and live are behind `AuthDefaults.SuperAdminPolicy`.
- Audit log entries (admin-visible only): live-view **start** and **end**
  (admin, device, monitor, start/end, duration) and stored-image **view/open**.
- The live socket authorizes per-device: a browser only receives frames for a device it
  started a session for; the server never pushes a device's frames to another group.

## Settings (new)
- Policy: `ScreenCapture.Enabled` (default false), `IntervalSeconds` (60),
  `Quality` (60), `MaxWidth` (1600), `LiveEnabled` (true when Enabled).
- Server: `ScreenshotStorage:Root` (path), `ScreenshotStorage:RetentionDays` (14),
  `ScreenLive:MaxSessionMinutes` (30), `ScreenLive:MaxFps` (8).

## Risks / limitations
- **AV/DLP false positives:** silent screen capture + an outbound frame socket resembles
  spyware; unsigned binaries (already a known repo limitation) raise the odds. Pilot on
  one PC; consider code-signing + an AV allowlist before rollout.
- **Lock screen / UAC secure desktop / some full-screen DRM & GPU-exclusive apps** are
  not captured from a user-session process (black/!blank frames). Documented, not fixed.
- **Multi-session / RDP:** one session helper per interactive session; live targets a
  chosen session (default the active console session). Multi-session selection UI is
  minimal in v1 (console session only) unless the builder finds it trivial.
- **Bandwidth/disk:** periodic at 60 s is modest; live at 5–8 fps JPEG is bounded by the
  latest-frame-wins relay. Retention caps disk.
- **CPU on the employee PC:** GDI BitBlt + JPEG at these rates is light; verify on the
  pilot.

## What is left to the builder
- Pipe-vs-second-pipe for binary frames (second pipe recommended); exact binary framing.
- GDI BitBlt specifics, multi-monitor enumeration, JPEG encoder settings, DPI handling.
- WebSocket library/auth handshake reusing device credentials + pinning; reconnect/backoff.
- SignalR hub-vs-group shape for the relay; per-viewer frame-dropping implementation.
- Screenshot table/index specifics, storage path scheme, streaming the image endpoint.
- Dashboard components (filmstrip, scrubber, lightbox, live canvas, monitor switcher).
- Live indicator overlay: exact look/size/corner, click-through + always-on-top window
  style, multi-monitor placement, show/hide tied to session lifecycle.
- Tests: capture engine (headless-safe), upload idempotency/drop-under-pressure, relay
  fan-out + backpressure, retention delete of files+rows, SUPERADMIN authorization,
  "no frames across devices" isolation, end-to-end live start/stop, and **indicator
  appears on start / is gone on stop/disconnect/timeout**.

## Open decisions for the owner — RESOLVED 2026-11-26

A builder-check review raised these; resolved with the owner:

1. **At-rest protection of stored screenshots:** owner chose **"just store the files"** —
   plaintext JPEGs on disk with basic file permissions, **no encryption at rest and no
   dedicated purge tool**. Time-based retention (14 days) is the only deletion path.
   Operational note recorded for the owner: the screenshot folder and server host must be
   access-controlled, since images are readable by anyone with file access.
2. **Disk safety (builder's #1):** builder's call — add a **free-space guard** that stops
   capture (and logs) before the volume can fill, so the screenshot store can never wedge
   PostgreSQL. Default folder on the install drive; docs recommend a separate volume.
3. **Live concurrency / monitor semantics (builder's #3):** builder's call — **one shared
   live stream per device**; concurrent SUPERADMIN watchers share the same view and the
   monitor switch is shared; **periodic capture pauses while a live session is active**.
4. **Per-device vs per-employee (builder's #4):** builder's call — **device-centric**
   timeline for v1 (matches the planned UI); `employee_id`/`session_user` are still stored
   per row so per-employee browsing can be added later without a migration.
5. **Enablement scope (builder's #5):** builder's call — reuse the existing policy
   **scopes** (global/department/device) so a single pilot PC can be enabled first;
   capture ships **off** at every scope.
6. **Capture scope:** builder's call — capture the **whole screen**; an app/window
   blocklist is a later enhancement, not v1.
7. **Network (builder's #6):** builder's call — build for the **LAN** topology in the
   constraints; roaming/off-LAN laptops degrade live view (latency, intermittent socket)
   but still capture stored stills best-effort.

Interface note (builder flag): `IScreenCaptureService` as it exists returns no image
bytes/monitor/streaming, so Task 5 **extends/redesigns** the capture abstraction rather
than literally implementing the current interface.
