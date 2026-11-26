"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import type { ScreenshotDto } from "@/lib/types";
import { useMonitorHub } from "@/providers/signalr-provider";

/** One stored screenshot thumbnail. Fetches its JPEG as an authenticated blob and renders an object URL. */
function ScreenshotThumb({ deviceId, shot, onOpen }: { deviceId: string; shot: ScreenshotDto; onOpen: (url: string) => void }) {
  const [url, setUrl] = useState<string | null>(null);

  useEffect(() => {
    let revoked = false;
    let objectUrl: string | null = null;
    api
      .screenshotBlob(deviceId, shot.id)
      .then((blob) => {
        if (revoked) return;
        objectUrl = URL.createObjectURL(blob);
        setUrl(objectUrl);
      })
      .catch(() => undefined);
    return () => {
      revoked = true;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [deviceId, shot.id]);

  return (
    <button
      type="button"
      onClick={() => url && onOpen(url)}
      className="group relative overflow-hidden rounded-lg border border-line bg-black/20 text-left"
      title={formatDateTime(shot.capturedAtUtc)}
    >
      {url ? (
        // eslint-disable-next-line @next/next/no-img-element
        <img src={url} alt={`Screen at ${formatDateTime(shot.capturedAtUtc)}`} className="aspect-video w-full object-cover" />
      ) : (
        <div className="flex aspect-video w-full items-center justify-center text-[11px] text-muted">loading…</div>
      )}
      <span className="absolute inset-x-0 bottom-0 bg-black/60 px-2 py-1 text-[11px] text-white">
        {formatDateTime(shot.capturedAtUtc)}
        {shot.monitor > 0 ? ` · mon ${shot.monitor}` : ""}
      </span>
    </button>
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
  const [shots, setShots] = useState<ScreenshotDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [lightbox, setLightbox] = useState<string | null>(null);

  const load = useCallback(() => {
    setError(null);
    api
      .screenshots(deviceId, { pageSize: 120 })
      .then((page) => setShots(page.items))
      .catch((e) => setError(e instanceof Error ? e.message : "Could not load screenshots."));
  }, [deviceId]);

  useEffect(() => load(), [load]);

  return (
    <div className="space-y-5">
      <LiveView deviceId={deviceId} displayCount={displayCount} />

      <div className="space-y-3">
        <div className="flex items-center justify-between">
          <h3 className="text-[13px] font-medium">Screenshot timeline</h3>
          <button type="button" onClick={load} className="text-[12px] text-blue-500 hover:underline">
            Refresh
          </button>
        </div>
        {error && <p className="text-[12px] text-red-500">{error}</p>}
        {shots && shots.length === 0 && (
          <p className="text-[12px] text-muted">No screenshots yet. They appear here once periodic capture is enabled for this device.</p>
        )}
        {shots === null && !error && <p className="text-[12px] text-muted">Loading…</p>}
        {shots && shots.length > 0 && (
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-4">
            {shots.map((shot) => (
              <ScreenshotThumb key={shot.id} deviceId={deviceId} shot={shot} onOpen={setLightbox} />
            ))}
          </div>
        )}
      </div>

      {lightbox && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/80 p-6"
          onClick={() => setLightbox(null)}
          role="dialog"
          aria-modal="true"
        >
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img src={lightbox} alt="Screenshot" className="max-h-full max-w-full rounded-lg" />
        </div>
      )}
    </div>
  );
}
