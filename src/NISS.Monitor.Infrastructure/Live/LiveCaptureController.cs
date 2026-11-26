using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Infrastructure.Capture;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Infrastructure.Live;

/// <summary>
/// Drives live screen capture in the session process: on a start command it shows the "being viewed" indicator and
/// streams the selected monitor at the requested fps; stop/disconnect/timeout cancels capture and hides the indicator.
/// </summary>
public sealed class LiveCaptureController(
    IScreenCaptureService capture,
    ILiveIndicator indicator,
    Func<Guid, int, byte[], CancellationToken, Task> frameSink,
    TimeProvider time) : IAsyncDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public bool Running { get; private set; }
    public int Monitor { get; private set; }
    public Guid SessionId { get; private set; }

    public void Start(LiveControlMessage msg)
    {
        lock (_gate)
        {
            StopCore();
            SessionId = msg.LiveSessionId;
            Monitor = msg.Monitor;
            Running = true;
            indicator.Show(msg.Monitor);
            var cts = new CancellationTokenSource();
            _cts = cts;
            var fps = Math.Clamp(msg.Fps, 1, 15);
            var quality = msg.Quality <= 0 ? 50 : msg.Quality;
            _loop = Task.Run(() => RunAsync(msg.LiveSessionId, fps, quality, cts.Token));
        }
    }

    /// <summary>Change the watched monitor. Moves the indicator too when streaming.</summary>
    public void Switch(int monitor)
    {
        lock (_gate)
        {
            Monitor = monitor;
            if (Running) indicator.Show(monitor);
        }
    }

    public void Stop()
    {
        lock (_gate) StopCore();
    }

    private void StopCore()
    {
        Running = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        indicator.Hide();
    }

    /// <summary>Capture one live frame of the current monitor and emit it. Returns false if capture failed.</summary>
    public async Task<bool> TickAsync(Guid session, int quality, CancellationToken ct)
    {
        var frame = await capture.CaptureMonitorAsync(Monitor, 1280, quality, ct).ConfigureAwait(false);
        if (frame is null) return false;
        await frameSink(session, Monitor, frame.Jpeg, ct).ConfigureAwait(false);
        return true;
    }

    private async Task RunAsync(Guid session, int fps, int quality, CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(1000.0 / fps);
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(session, quality, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch { /* a failed frame must not stop the stream */ }
            try { await Task.Delay(delay, time, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch { /* ignore */ }
        }
    }
}
