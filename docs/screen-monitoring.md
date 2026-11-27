# Screen monitoring

Three separate capabilities, each switched off until a policy turns it on:

| Capability | What it does | Stored? |
|---|---|---|
| **Automatic screenshots** | A still of the whole desktop every *N* minutes | Yes, as JPEG files + a `screenshots` row |
| **Flagged activity** | A still (and optionally a short clip) the moment a flagged site or app comes to the foreground | Yes, marked `trigger=FLAGGED` |
| **Live view** | A superadmin watches the screen on demand | No - frames are relayed, never written to disk |

All of it is configured under **Settings -> Agent policies -> Screen capture**. Retention lives under
**Settings -> Retention**.

## Where to configure what

Everything is part of the agent policy, so it follows the usual scope precedence: **global < department < device**.
To flag different sites on different PCs, create a device-scoped policy (Settings -> Agent policies -> New -> Single
device) and set its flagged terms; it overrides the global one for that machine.

| Setting | Meaning |
|---|---|
| Automatic screenshots | Master switch for the timed stills |
| Capture every (minutes) | Interval between stills. Stored on the wire as `intervalSeconds`; the UI edits whole minutes |
| Image quality (1-100) | JPEG quality. Lower = smaller files |
| Max width (px) | Wide/multi-monitor desktops are downscaled to this width before encoding |
| Live screen view | Allows on-demand live view for this scope |
| Capture on flagged activity | Master switch for flagged capture |
| Flagged sites & apps | One term per line (see [matching](#how-a-flagged-site-is-recognised)) |
| Cooldown per term | Minimum gap before the same term fires again |
| Also record a clip | Record a burst of frames after the flag, not just one still |
| Clip length / frame rate | Burst length in seconds and frames per second |

## How a flagged site is recognised

**There is no URL capture in this system.** `IBrowserMonitoringProvider` is deliberately inert, and no payload type
carries a web address. A flagged term is a **case-insensitive substring** matched against:

1. the **foreground window title** - this is how a website is recognised, because browsers put the page title in the
   window title (`Facebook - Google Chrome` matches the term `facebook`); then
2. the **foreground process name** - so `steam.exe` or `vlc` can be flagged as applications.

The match kind is recorded per capture as `matchedOn` = `TITLE` or `PROCESS` and shown in the dashboard.

### Domain terms also match the site's name

Administrators naturally type a domain, but **a window title contains the site's name, not its address**. A real
recorded title looks like:

```
"...Song With Lyrics | Sunny Leone | Mustafa Zahid - YouTube - Brave"
```

There is no `.com` anywhere in it, so a literal `youtube.com` would never fire. An address-like term is therefore
also matched by its **registrable label**:

| Term as typed | Also matched as |
|---|---|
| `youtube.com` | `youtube` |
| `https://www.youtube.com/feed` | `youtube.com`, `youtube` |
| `bbc.co.uk` | `bbc` |
| `mail.google.com` | `google` |
| `youtube` | (already a bare name) |
| `steam.exe` | (an executable, not a domain) |

The scheme, `www.`, port, path and query are stripped first. Two-part public suffixes (`co.uk`, `com.au`, …) are
handled from a short built-in list, so `bbc.co.uk` yields `bbc` and not `co`. A capture always reports the term
**as the administrator typed it**, not the derived form.

Consequences worth telling an administrator:

* A site whose page title contains neither form is **not** caught. Titles vary by page and by language.
* A term can **false-positive**: a document named `facebook notes.docx` matches the term `facebook`. Deriving the
  bare label widens this slightly - that is the deliberate trade for `youtube.com` working at all.
* Flagged matching depends on the **`captureWindowTitles`** privacy toggle. With window titles off, only
  process-name terms can ever match; the policy editor warns about this combination.
* Terms are plain substrings, not wildcards or regular expressions.

### Firing rules

`FlaggedActivityDetector` (in `NISS.Monitor.Core`, pure and unit-tested) decides when a capture happens:

* It fires on a **transition**, not on every poll - sitting on a flagged window does not capture once a second.
* It fires at most once per **cooldown** window per term, so one flagged site cannot flood storage.
* Cooldown is tracked **per term**, so one noisy site does not mask another.
* Blank/whitespace terms are ignored (an empty substring would otherwise match every window).

## Clips are frame sequences, not video files

There is no video encoder in this project (no ffmpeg, no mp4/webm). A "clip" is a burst of JPEG stills that share a
`clipId`, numbered by `clipSeq`, which the dashboard replays as a scrubbable flipbook using the captured timestamps
for real-time pacing.

* `clipSeconds x clipFps` frames, clamped to `FlaggedActivityPolicy.MaxClipFrames` (120) so a mistyped policy cannot
  emit thousands of images.
* The lead still is captured **first and has no `clipId`**, so there is evidence even if the clip is cut short.
* If the session locks or ends mid-clip the burst is abandoned. The clip then reports fewer frames than
  `expectedFrameCount`, and the dashboard says so rather than pretending the clip is complete.
* In the timeline a clip occupies a **single tile**; `includeClipFrames=true` lists every frame.

## What is never captured

* Nothing at all while the **workstation is locked** or the **user is idle** - this gate applies to flagged captures
  too, so a flag cannot photograph a lock screen.
* **Live view frames are never written to disk.** Only periodic and flagged captures are stored.
* Flagged capture works **independently** of automatic screenshots: you can record only flagged activity and keep no
  rolling screenshot trail.

## Retention (automatic deletion)

The nightly retention job deletes the JPEG **files as well as the database rows**. Periodic and flagged captures
expire on **separate schedules**, because flagged captures are the evidence someone comes back to:

| Setting | Default | Falls back to |
|---|---|---|
| Screenshots | 14 days | `ScreenshotStorage:RetentionDays` |
| Flagged screenshots & clips | 90 days | `ScreenshotStorage:FlaggedRetentionDays` |

`0` means keep forever. The dashboard warns if flagged retention is set shorter than periodic retention. A settings
row written before these fields existed reads as null and falls back to the `serversettings.json` values above.

`ScreenshotStorage:MinFreeDiskMb` (default 2048) is a hard floor: uploads are refused with **507** once the volume
drops below it, so screenshots cannot fill the disk and wedge the database.

## Access control and audit

* Reading the timeline, any image, any clip, and live view is **SUPERADMIN only**. ADMIN and VIEWER get 403.
* Every image and clip view writes a `SCREENSHOT_VIEWED` audit entry naming the device and the capture.
* Clips and images are scoped to their device: a clip id from one device 404s on another.
* The service only accepts frames from the **installed session agent** over the screen pipe (verified by full image
  path), so a local user cannot inject fabricated screenshots.
* The server normalises an unknown `trigger` to `PERIODIC` and **discards** flag/clip fields on a non-flagged upload,
  so a tampered agent cannot fabricate flagged evidence.

## How a capture travels

```
session agent (user desktop)            service (LocalSystem)              server
  GdiScreenCaptureService
  ScreenCaptureLoop ....... interval
  FlaggedActivityDetector . foreground --> screen pipe (JSON header + JPEG)
                                              ScreenshotUploader (bounded,
                                              drop-oldest queue)
                                                    --> POST /devices/screenshots
                                                            FileScreenshotStore
                                                            (file on disk + row)
```

The session agent stamps each stored frame with its own Windows username: the service runs as LocalSystem and would
otherwise attribute every shared-PC capture to `SYSTEM`. The upload queue is sized to hold a whole clip plus periodic
stills; under a longer backlog the **oldest** frame is dropped rather than retried forever, because images are lower
value than events and must never block event upload.

File layout under `ScreenshotStorage:Root`:

```
<device code>/<yyyy-MM-dd>/<ticks>-<monitor>[-cNNNN]-<row id>.jpg
```

The row id is part of the name so two captures can never share a file - clip frames are milliseconds apart, and a
flagged still can land in the same tick as a periodic one.

## Enabling it, end to end

1. **Settings -> Agent policies**, pick the scope (global, or create a device-scoped policy for one PC).
2. Under **Screen capture**, switch on *Automatic screenshots* and set *Capture every*.
3. For flagged activity, switch on *Capture on flagged activity* and list the sites/apps. Leave
   *Capture window titles* on (under Privacy) or only process names will match.
4. Save. Agents apply the new policy on their next config poll (within about a minute).
5. **Settings -> Retention**: set how long periodic and flagged captures are kept.
6. View captures on the device page, **Screens** tab (SUPERADMIN): filter All / Flagged / Periodic, and open a
   clip tile to replay it.
