using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Infrastructure.Live;

/// <summary>Local presence as the session process sees it: used to skip capture when the screen is locked or the user is idle.</summary>
public readonly record struct PresenceSnapshot(bool Locked, bool Idle);

/// <summary>
/// Periodic screenshot capture for the session process. Gated: it captures only when the policy is enabled and the
/// session is unlocked and active, so nothing is captured when the feature is off, the workstation is locked, or the
/// user is away. Captured frames are handed to <paramref name="sink"/> (the screen pipe to the service).
/// </summary>
public sealed class ScreenCaptureLoop(
    IScreenCaptureService capture,
    Func<ScreenCapturePolicy> policy,
    Func<PresenceSnapshot> presence,
    Func<ScreenFrameHeader, byte[], CancellationToken, Task> sink,
    TimeProvider time)
{
    /// <summary>One gated capture attempt. Returns true only when a frame was captured and emitted.</summary>
    public async Task<bool> TickAsync(CancellationToken ct)
    {
        var p = policy();
        if (!p.Enabled) return false;

        var now = presence();
        if (now.Locked || now.Idle) return false;

        // -1 = the whole virtual desktop, so periodic stills record every monitor.
        var frame = await capture.CaptureMonitorAsync(-1, p.MaxWidth, p.Quality, ct).ConfigureAwait(false);
        if (frame is null) return false;

        var header = new ScreenFrameHeader(ScreenPipeKind.Periodic, frame.MonitorIndex, frame.Width, frame.Height,
            time.GetUtcNow().UtcTicks, frame.Jpeg.Length);
        await sink(header, frame.Jpeg, ct).ConfigureAwait(false);
        return true;
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
}
