using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Engines;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Infrastructure.Live;

/// <summary>Local presence as the session process sees it: used to skip capture when the screen is locked or the user is idle.</summary>
public readonly record struct PresenceSnapshot(bool Locked, bool Idle);

/// <summary>
/// Periodic screenshot capture for the session process. Gated: it captures only when the policy is enabled and the
/// session is unlocked and active, so nothing is captured when the feature is off, the workstation is locked, or the
/// user is away. Captured frames are handed to <paramref name="sink"/> (the screen pipe to the service).
/// </summary>
/// <remarks>
/// The same gate guards flagged captures (see <see cref="CaptureFlaggedAsync"/>): a locked or idle machine is never
/// photographed, even by a flag. Flagged capture does however work while periodic capture is switched off, so an
/// administrator can record only flagged activity without a rolling screenshot trail.
/// </remarks>
public sealed class ScreenCaptureLoop(
    IScreenCaptureService capture,
    Func<ScreenCapturePolicy> policy,
    Func<PresenceSnapshot> presence,
    Func<ScreenFrameHeader, byte[], CancellationToken, Task> sink,
    TimeProvider time)
{
    /// <summary>One gated capture attempt on the periodic schedule. Returns true only when a frame was captured and emitted.</summary>
    public async Task<bool> TickAsync(CancellationToken ct)
    {
        var p = policy();
        if (!p.Enabled) return false;
        if (!IsPresent()) return false;
        return await CaptureOneAsync(p, ScreenTriggers.Periodic, null, null, null, 0, 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Captures the evidence for one flagged match: a single still, plus a clip of
    /// <see cref="FlaggedActivityPolicy.ClipFrameCount"/> frames when clips are enabled. Returns the number of frames
    /// emitted. Honours the same lock/idle gate as periodic capture.
    /// </summary>
    public async Task<int> CaptureFlaggedAsync(FlaggedMatch match, CancellationToken ct)
    {
        var p = policy();
        var flagged = p.Flagged;
        if (!flagged.Enabled) return 0;
        if (!IsPresent()) return 0;

        // The still is always taken first so there is evidence even if the clip is interrupted by a lock or shutdown.
        var emitted = await CaptureOneAsync(p, ScreenTriggers.Flagged, match.Term, match.MatchedOn, null, 0, 0, ct).ConfigureAwait(false) ? 1 : 0;
        if (!flagged.CaptureClip) return emitted;

        var total = flagged.ClipFrameCount;
        var clipId = Guid.NewGuid();
        // Same clamp ClipFrameCount uses, so the clip really lasts ClipSeconds rather than finishing early.
        var delay = TimeSpan.FromSeconds(1.0 / FlaggedActivityPolicy.ClampFps(flagged.ClipFps));
        for (var seq = 0; seq < total; seq++)
        {
            if (ct.IsCancellationRequested) break;
            // A lock mid-clip stops it: the rest of the burst is abandoned rather than captured behind the lock screen.
            if (!IsPresent()) break;
            if (await CaptureOneAsync(p, ScreenTriggers.Flagged, match.Term, match.MatchedOn, clipId, seq, total, ct).ConfigureAwait(false))
                emitted++;
            if (seq + 1 < total)
            {
                try { await Task.Delay(delay, time, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        return emitted;
    }

    /// <summary>Captures on the policy's interval until cancelled. The interval is re-read each cycle (hot-applied).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch { /* a failed capture must not kill the loop */ }

            var interval = Math.Max(5, policy().IntervalSeconds);
            try { await Task.Delay(TimeSpan.FromSeconds(interval), time, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private bool IsPresent()
    {
        var now = presence();
        return !now.Locked && !now.Idle;
    }

    private async Task<bool> CaptureOneAsync(
        ScreenCapturePolicy p, string trigger, string? term, string? matchedOn,
        Guid? clipId, int seq, int total, CancellationToken ct)
    {
        // -1 = the whole virtual desktop, so stills record every monitor.
        var frame = await capture.CaptureMonitorAsync(-1, p.MaxWidth, p.Quality, ct).ConfigureAwait(false);
        if (frame is null) return false;

        var header = new ScreenFrameHeader(ScreenPipeKind.Periodic, frame.MonitorIndex, frame.Width, frame.Height,
            time.GetUtcNow().UtcTicks, frame.Jpeg.Length, trigger, term, matchedOn, clipId, seq, total);
        await sink(header, frame.Jpeg, ct).ConfigureAwait(false);
        return true;
    }
}
