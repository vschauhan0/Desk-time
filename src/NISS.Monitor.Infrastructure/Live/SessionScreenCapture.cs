using System.Runtime.Versioning;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
using NISS.Monitor.Core.Engines;
using NISS.Monitor.Infrastructure.Capture;
using NISS.Monitor.Infrastructure.Ipc;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Infrastructure.Live;

/// <summary>
/// Session-process composition of screen monitoring: hosts the control pipe (service -&gt; session), connects the binary
/// frame pipe (session -&gt; service), runs the policy-gated periodic loop, and drives live capture + the indicator on
/// command. Entirely best-effort and inert until policy enables capture; failures never crash the session helper.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionScreenCapture(
    IScreenCaptureService capture,
    IIdleMonitor idle,
    Func<bool> isLocked,
    Action<string>? log = null) : IAsyncDisposable
{
    private volatile ScreenCapturePolicy _policy = new(false, 60, 60, 1600, true, FlaggedActivityPolicy.Default);
    private ScreenPipeClient? _frames;
    private LiveCaptureController? _controller;
    private ILiveIndicator? _indicator;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly FlaggedActivityDetector _flagged = new(TimeProvider.System);
    private readonly SemaphoreSlim _flagLock = new(1, 1);
    private ScreenCaptureLoop? _loop;
    private CancellationToken _ct;

    /// <summary>
    /// Feeds the foreground window to the flagged-activity detector. Called by the session runtime on its normal
    /// foreground poll; a match captures a still (and a clip) on a background task so the caller is never blocked.
    /// Entirely best-effort: a capture failure is swallowed and never disturbs event collection.
    /// </summary>
    public void NotifyForeground(string? processName, string? windowTitle)
    {
        try
        {
            var match = _flagged.Evaluate(_policy.Flagged, processName, windowTitle);
            if (match is null) return;
            log?.Invoke($"flagged activity: '{match.Term}' matched on {match.MatchedOn}");
            _ = CaptureFlaggedAsync(match, _ct);
        }
        catch { /* detection must never take the session helper down */ }
    }

    private async Task CaptureFlaggedAsync(FlaggedMatch match, CancellationToken ct)
    {
        // One flagged capture at a time: overlapping clips would interleave frames and multiply storage.
        if (!await _flagLock.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            if (_loop is { } loop) await loop.CaptureFlaggedAsync(match, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log?.Invoke("flagged capture failed: " + ex.Message); }
        finally { _flagLock.Release(); }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _ct = ct;
        try
        {
            // Live view currently shows no on-screen notice (periodic capture is silent too). To re-enable the
            // employee notice, swap NullLiveIndicator for: new LiveIndicatorOverlay(m => capture.EnumerateMonitors()...).
            _indicator = new NullLiveIndicator();
            _frames = new ScreenPipeClient(AgentConstants.ScreenPipeName);
            _controller = new LiveCaptureController(capture, _indicator, SendLiveAsync, TimeProvider.System);

            var loop = new ScreenCaptureLoop(capture, () => _policy, Presence, SendPeriodicAsync, TimeProvider.System);
            _loop = loop;
            await using var control = new ScreenControlPipeServer(HandleCommandAsync);

            _ = KeepFramePipeConnectedAsync(ct);
            await Task.WhenAny(control.RunAsync(ct), loop.RunAsync(ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log?.Invoke("screen capture stopped: " + ex.Message); }
    }

    private PresenceSnapshot Presence()
    {
        try { return new PresenceSnapshot(isLocked(), idle.GetIdleTime() > TimeSpan.FromMinutes(5)); }
        catch { return new PresenceSnapshot(false, false); }
    }

    private async Task KeepFramePipeConnectedAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_frames is { IsConnected: false }) await _frames.ConnectAsync(ct).ConfigureAwait(false);
            }
            catch { /* service pipe not up yet */ }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Stored frames carry the session's own username: the service runs as LocalSystem and cannot determine it.
    private Task SendPeriodicAsync(ScreenFrameHeader header, byte[] jpeg, CancellationToken ct) =>
        SendAsync(header with { SessionUser = Environment.UserName }, jpeg, ct);

    private Task SendLiveAsync(Guid session, int monitor, byte[] jpeg, CancellationToken ct) =>
        SendAsync(new ScreenFrameHeader(ScreenPipeKind.Live, monitor, 0, 0, DateTimeOffset.UtcNow.UtcTicks, jpeg.Length), jpeg, ct);

    private async Task SendAsync(ScreenFrameHeader header, byte[] jpeg, CancellationToken ct)
    {
        if (_frames is not { IsConnected: true }) return;
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _frames.SendFrameAsync(header, jpeg, ct).ConfigureAwait(false); }
        catch { /* drop on pipe error; reconnect loop recovers */ }
        finally { _sendLock.Release(); }
    }

    private Task HandleCommandAsync(ScreenCommand cmd, CancellationToken ct)
    {
        switch (cmd.Kind)
        {
            case ScreenCommandKind.SetPolicy when cmd.Policy is not null: _policy = cmd.Policy; break;
            case ScreenCommandKind.LiveStart when cmd.Live is not null: _controller?.Start(cmd.Live); break;
            case ScreenCommandKind.LiveStop: _controller?.Stop(); break;
            case ScreenCommandKind.LiveSwitch when cmd.Live is not null: _controller?.Switch(cmd.Live.Monitor); break;
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_controller is not null) await _controller.DisposeAsync().ConfigureAwait(false);
        (_indicator as IDisposable)?.Dispose();
        if (_frames is not null) await _frames.DisposeAsync().ConfigureAwait(false);
        _sendLock.Dispose();
        _flagLock.Dispose();
    }
}
