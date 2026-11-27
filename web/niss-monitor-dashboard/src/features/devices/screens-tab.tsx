"use client";

import { Flag, Pause, Play } from "lucide-react";
import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import type { ScreenshotClipDto, ScreenshotDto } from "@/lib/types";
import { useMonitorHub } from "@/providers/signalr-provider";

/** Fetches one screenshot's JPEG as an authenticated blob and exposes it as an object URL, revoked on unmount. */
function useScreenshotUrl(deviceId: string, id: string | null): string | null {
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    if (!id) return;
    let cancelled = false;
    let objectUrl: string | null = null;
    api
      .screenshotBlob(deviceId, id)
      .then((blob) => {
        if (cancelled) return;
        objectUrl = URL.createObjectURL(blob);
        setUrl(objectUrl);
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [deviceId, id]);
  return url;
}

/** One stored screenshot thumbnail. Flagged captures are badged, and a clip opens the replay player. */
function ScreenshotThumb({ deviceId, shot, onOpen }: { deviceId: string; shot: ScreenshotDto; onOpen: (target: Overlay) => void }) {
  const url = useScreenshotUrl(deviceId, shot.id);
  const isClip = shot.clipId !== null;
  const flagged = shot.trigger === "FLAGGED";

  return (
    <button
      type="button"
      onClick={() => {
        if (isClip && shot.clipId) onOpen({ kind: "clip", clipId: shot.clipId, flagTerm: shot.flagTerm });
        else if (url) onOpen({ kind: "still", url });
      }}
      className="group relative overflow-hidden rounded-lg border border-line bg-black/20 text-left"
      title={flagged ? `Flagged: ${shot.flagTerm ?? "activity"} (${formatDateTime(shot.capturedAtUtc)})` : formatDateTime(shot.capturedAtUtc)}
    >
      {url ? (
        // eslint-disable-next-line @next/next/no-img-element
        <img src={url} alt={`Screen at ${formatDateTime(shot.capturedAtUtc)}`} className="aspect-video w-full object-cover" />
      ) : (
        <div className="flex aspect-video w-full items-center justify-center text-[11px] text-muted">loading…</div>
      )}
      {flagged && (
        <span className="absolute left-1.5 top-1.5 flex items-center gap-1 rounded bg-red-600/90 px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-white">
          <Flag size={10} aria-hidden />
          {shot.flagTerm ?? "flagged"}
        </span>
      )}
      {isClip && (
        <span className="absolute right-1.5 top-1.5 flex items-center gap-1 rounded bg-black/75 px-1.5 py-0.5 text-[10px] font-medium text-white">
          <Play size={10} aria-hidden />
          clip
        </span>
      )}
      <span className="absolute inset-x-0 bottom-0 bg-black/60 px-2 py-1 text-[11px] text-white">
        {formatDateTime(shot.capturedAtUtc)}
        {shot.monitor > 0 ? ` · mon ${shot.monitor}` : ""}
      </span>
    </button>
  );
}

/** Which captures the timeline is showing. */
type TimelineFilter = "ALL" | "FLAGGED" | "PERIODIC";

/** What the full-screen overlay is showing. */
type Overlay = { kind: "still"; url: string } | { kind: "clip"; clipId: string; flagTerm: string | null };

/**
 * Replays one flagged clip. A clip is a sequence of JPEG stills, not a video file, so playback is a timer over
 * pre-fetched frames with a scrubber. Frame delays come from the captured timestamps, so playback matches real time.
 */
function ClipPlayer({ deviceId, clipId }: { deviceId: string; clipId: string }) {
  const [clip, setClip] = useState<ScreenshotClipDto | null>(null);
  const [urls, setUrls] = useState<(string | null)[]>([]);
  const [index, setIndex] = useState(0);
  const [playing, setPlaying] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Load the frame list, then all frame images. Object URLs are revoked together when the player closes.
  useEffect(() => {
    let cancelled = false;
    const created: string[] = [];
    api
      .screenshotClip(deviceId, clipId)
      .then(async (c) => {
        if (cancelled) return;
        setClip(c);
        const blobs = await Promise.all(
          c.frames.map((f) => api.screenshotBlob(deviceId, f.id).catch(() => null)),
        );
        if (cancelled) return;
        const next = blobs.map((b) => {
          if (!b) return null;
          const u = URL.createObjectURL(b);
          created.push(u);
          return u;
        });
        setUrls(next);
      })
      .catch((e) => !cancelled && setError(e instanceof Error ? e.message : "Could not load the clip."));
    return () => {
      cancelled = true;
      for (const u of created) URL.revokeObjectURL(u);
    };
  }, [deviceId, clipId]);

  const frameCount = clip?.frames.length ?? 0;

  // Real-time playback: wait the gap between this frame's capture time and the next one's.
  useEffect(() => {
    if (!playing || frameCount === 0 || urls.length === 0) return;
    const frames = clip!.frames;
    const current = frames[index];
    const next = frames[index + 1];
    const gap = next ? new Date(next.capturedAtUtc).getTime() - new Date(current.capturedAtUtc).getTime() : 600;
    const delay = Math.min(Math.max(Number.isFinite(gap) ? gap : 500, 80), 3000);
    const timer = setTimeout(() => setIndex((i) => (i + 1) % frameCount), delay);
    return () => clearTimeout(timer);
  }, [playing, index, frameCount, urls.length, clip]);

  if (error) return <p className="text-[13px] text-red-400">{error}</p>;
  if (!clip) return <p className="text-[13px] text-muted">Loading clip…</p>;

  const url = urls[index] ?? null;
  const incomplete = clip.expectedFrameCount > 0 && clip.frameCount < clip.expectedFrameCount;

  return (
    <div className="w-full max-w-5xl space-y-3" onClick={(e) => e.stopPropagation()} role="document">
      <div className="flex flex-wrap items-center gap-3 text-[12px] text-white">
        <span className="flex items-center gap-1.5 rounded bg-red-600/90 px-2 py-0.5 text-[11px] font-semibold uppercase tracking-wide">
          <Flag size={11} aria-hidden />
          {clip.flagTerm ?? "flagged"}
        </span>
        <span>{formatDateTime(clip.startedAtUtc)}</span>
        <span className="text-white/60">
          matched on {clip.matchedOn === "PROCESS" ? "process name" : "window title"}
        </span>
      </div>
      <div className="flex aspect-video w-full items-center justify-center overflow-hidden rounded-lg bg-black">
        {url ? (
          // eslint-disable-next-line @next/next/no-img-element
          <img src={url} alt={`Clip frame ${index + 1} of ${clip.frameCount}`} className="h-full w-full object-contain" />
        ) : (
          <span className="text-[12px] text-muted">{urls.length === 0 ? "Loading frames…" : "Frame unavailable"}</span>
        )}
      </div>
      <div className="flex items-center gap-3">
        <button
          type="button"
          onClick={() => setPlaying((p) => !p)}
          className="flex items-center gap-1.5 rounded-md bg-white/15 px-3 py-1.5 text-[12px] font-medium text-white hover:bg-white/25"
        >
          {playing ? <Pause size={13} aria-hidden /> : <Play size={13} aria-hidden />}
          {playing ? "Pause" : "Play"}
        </button>
        <input
          type="range"
          min={0}
          max={Math.max(0, clip.frameCount - 1)}
          value={index}
          onChange={(e) => {
            setPlaying(false);
            setIndex(Number(e.target.value));
          }}
          className="h-1.5 flex-1 accent-[var(--brand)]"
          aria-label="Clip position"
        />
        <span className="mono w-16 shrink-0 text-right text-[11px] text-white/70">
          {index + 1}/{clip.frameCount}
        </span>
      </div>
      <p className="text-[11px] text-white/50">
        A clip is a sequence of stills replayed here, not a video file.
        {incomplete && ` Only ${clip.frameCount} of an expected ${clip.expectedFrameCount} frames were captured — the session locked or ended mid-clip.`}
      </p>
    </div>
  );
}

/** Live view pane: starts a session, joins the SignalR group, renders incoming frames, stops on unmount. */
function LiveView({ deviceId, displayCount }: { deviceId: string; displayCount: number }) {
  const { watchLive } = useMonitorHub();
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [monitor, setMonitor] = useState(0);
  const [frame, setFrame] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [starting, setStarting] = useState(false);
  const stopRef = useRef<(() => void) | null>(null);
  const sessionRef = useRef<string | null>(null);

  // "Stop" leaves the shared session (StopWatchLive via the hub). The server ends the session only when the last
  // watcher leaves, so one admin stopping does not cut off another who is still watching the same device.
  const stop = useCallback(() => {
    stopRef.current?.();
    stopRef.current = null;
    sessionRef.current = null;
    setSessionId(null);
    setFrame(null);
  }, []);

  const start = useCallback(async () => {
    setStarting(true);
    setError(null);
    try {
      const session = await api.startLiveView(deviceId, monitor);
      sessionRef.current = session.liveSessionId;
      setSessionId(session.liveSessionId);
      stopRef.current = watchLive(session.liveSessionId, (f) => {
        setMonitor(f.monitor);
        setFrame(`data:image/jpeg;base64,${f.jpegBase64}`);
      });
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not start live view.");
    } finally {
      setStarting(false);
    }
  }, [deviceId, monitor, watchLive]);

  // Stop the session when the pane unmounts (tab change / navigation) or the page is hidden. A hard crash / tab close
  // is caught server-side: the SignalR connection drops and the hub's OnDisconnected ends the session.
  useEffect(() => {
    const onHide = () => {
      if (document.visibilityState === "hidden") stop();
    };
    document.addEventListener("visibilitychange", onHide);
    return () => {
      document.removeEventListener("visibilitychange", onHide);
      stopRef.current?.();
      sessionRef.current = null;
    };
  }, [stop]);

  const switchMonitor = (m: number) => {
    setMonitor(m);
    if (sessionId) api.switchLiveView(deviceId, sessionId, m).catch(() => undefined);
  };

  return (
    <div className="space-y-3 rounded-lg border border-line p-4">
      <div className="flex flex-wrap items-center gap-3">
        <h3 className="text-[13px] font-medium">Live screen</h3>
        {sessionId ? (
          <button type="button" onClick={stop} className="rounded-md bg-red-600 px-3 py-1.5 text-[12px] font-medium text-white">
            Stop
          </button>
        ) : (
          <button
            type="button"
            onClick={start}
            disabled={starting}
            className="rounded-md bg-blue-600 px-3 py-1.5 text-[12px] font-medium text-white disabled:opacity-50"
          >
            {starting ? "Starting…" : "Watch live"}
          </button>
        )}
        {displayCount > 1 && (
          <div className="flex items-center gap-1">
            {Array.from({ length: Math.min(displayCount, 6) }, (_, i) => (
              <button
                key={i}
                type="button"
                onClick={() => switchMonitor(i)}
                className={`rounded px-2 py-1 text-[11px] ${monitor === i ? "bg-blue-600 text-white" : "border border-line text-muted"}`}
              >
                Mon {i}
              </button>
            ))}
          </div>
        )}
      </div>
      {error && <p className="text-[12px] text-red-500">{error}</p>}
      <div className="flex aspect-video w-full items-center justify-center overflow-hidden rounded-md border border-line bg-black/40">
        {frame ? (
          // eslint-disable-next-line @next/next/no-img-element
          <img src={frame} alt="Live screen" className="h-full w-full object-contain" />
        ) : (
          <span className="text-[12px] text-muted">
            {sessionId ? "Waiting for frames…" : "Not watching. Press “Watch live”."}
          </span>
        )}
      </div>
      <p className="text-[11px] text-muted">
        The employee sees a small on-screen notice while you watch. Live frames are not recorded.
      </p>
    </div>
  );
}

/** SUPERADMIN-only tab: stored screenshot timeline + live view. */
export function ScreensTab({ deviceId, displayCount = 1 }: { deviceId: string; displayCount?: number }) {
  const [loaded, setLoaded] = useState<{ filter: TimelineFilter; items: ScreenshotDto[] } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [overlay, setOverlay] = useState<Overlay | null>(null);
  const [filter, setFilter] = useState<TimelineFilter>("ALL");

  // Nothing is set synchronously here: this runs straight from an effect, and a synchronous setState would
  // cascade a second render on every filter change.
  const load = useCallback(() => {
    api
      .screenshots(deviceId, { pageSize: 120, ...(filter === "ALL" ? {} : { trigger: filter }) })
      .then((page) => {
        setLoaded({ filter, items: page.items });
        setError(null);
      })
      .catch((e) => setError(e instanceof Error ? e.message : "Could not load screenshots."));
  }, [deviceId, filter]);

  useEffect(() => load(), [load]);

  // Derived rather than cleared on filter change, so switching filters never renders tiles from the previous one.
  const shots = loaded && loaded.filter === filter ? loaded.items : null;

  const empty =
    filter === "FLAGGED"
      ? "No flagged captures for this device. They appear here when a flagged site or app comes to the foreground."
      : "No screenshots yet. They appear here once automatic capture is enabled for this device.";

  return (
    <div className="space-y-5">
      <LiveView deviceId={deviceId} displayCount={displayCount} />

      <div className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h3 className="text-[13px] font-medium">Screenshot timeline</h3>
          <div className="flex items-center gap-3">
            <div className="flex items-center gap-1" role="group" aria-label="Filter screenshots">
              {(["ALL", "FLAGGED", "PERIODIC"] as readonly TimelineFilter[]).map((key) => (
                <button
                  key={key}
                  type="button"
                  onClick={() => setFilter(key)}
                  aria-pressed={filter === key}
                  className={`rounded px-2 py-1 text-[11px] ${filter === key ? "bg-blue-600 text-white" : "border border-line text-muted"}`}
                >
                  {key === "ALL" ? "All" : key === "FLAGGED" ? "Flagged" : "Periodic"}
                </button>
              ))}
            </div>
            <button type="button" onClick={load} className="text-[12px] text-blue-500 hover:underline">
              Refresh
            </button>
          </div>
        </div>
        <p className="text-[11px] text-faint">
          A clip is shown as one tile; open it to replay every frame.
        </p>
        {error && <p className="text-[12px] text-red-500">{error}</p>}
        {shots && shots.length === 0 && <p className="text-[12px] text-muted">{empty}</p>}
        {shots === null && !error && <p className="text-[12px] text-muted">Loading…</p>}
        {shots && shots.length > 0 && (
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-4">
            {shots.map((shot) => (
              <ScreenshotThumb key={shot.id} deviceId={deviceId} shot={shot} onOpen={setOverlay} />
            ))}
          </div>
        )}
      </div>

      {overlay && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/80 p-6"
          onClick={() => setOverlay(null)}
          role="dialog"
          aria-modal="true"
          aria-label={overlay.kind === "clip" ? "Flagged activity clip" : "Screenshot"}
        >
          {overlay.kind === "still" ? (
            // eslint-disable-next-line @next/next/no-img-element
            <img src={overlay.url} alt="Screenshot" className="max-h-full max-w-full rounded-lg" />
          ) : (
            <ClipPlayer deviceId={deviceId} clipId={overlay.clipId} />
          )}
        </div>
      )}
    </div>
  );
}
