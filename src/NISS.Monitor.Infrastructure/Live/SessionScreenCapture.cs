using System.Runtime.Versioning;
using NISS.Monitor.Core;
using NISS.Monitor.Core.Abstractions;
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
    private volatile ScreenCapturePolicy _policy = new(false, 60, 60, 1600, true);
    private ScreenPipeClient? _frames;
    private LiveCaptureController? _controller;
    private LiveIndicatorOverlay? _indicator;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            _indicator = new LiveIndicatorOverlay(m => capture.EnumerateMonitors().FirstOrDefault(x => x.Index == m));
            _frames = new ScreenPipeClient(AgentConstants.ScreenPipeName);
            _controller = new LiveCaptureController(capture, _indicator, SendLiveAsync, TimeProvider.System);

            var loop = new ScreenCaptureLoop(capture, () => _policy, Presence, SendPeriodicAsync, TimeProvider.System);
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

    private Task SendPeriodicAsync(ScreenFrameHeader header, byte[] jpeg, CancellationToken ct) => SendAsync(header, jpeg, ct);

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
        _indicator?.Dispose();
        if (_frames is not null) await _frames.DisposeAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }
}
