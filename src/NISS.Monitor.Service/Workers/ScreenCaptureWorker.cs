using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NISS.Monitor.Core;
using NISS.Monitor.Infrastructure.Http;
using NISS.Monitor.Infrastructure.Identity;
using NISS.Monitor.Infrastructure.Ipc;
using NISS.Monitor.Infrastructure.Live;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Service.Workers;

/// <summary>
/// Service-side screen monitoring: receives binary frames from the session (screen pipe), uploads periodic stills and
/// relays live frames up the server websocket; pushes effective policy and live commands down to the session (control
/// pipe). Entirely best-effort and inert until the server policy enables capture.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScreenCaptureWorker(
    ServerApiClient api,
    DeviceIdentityStore identity,
    ServerTrust trust,
    ILogger<ScreenCaptureWorker> log) : BackgroundService
{
    private readonly object _liveLock = new();
    private Guid _currentLiveSession;
    private LiveSocketClient? _socket;

    private Guid CurrentLiveSession
    {
        get { lock (_liveLock) return _currentLiveSession; }
        set { lock (_liveLock) _currentLiveSession = value; }
    }
    private ScreenControlPipeClient? _control;
    private ScreenshotUploader? _uploader;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _uploader = new ScreenshotUploader(new ServerScreenshotUpload(api), capacity: 32);
        await using var screenServer = new ScreenPipeServer(AgentConstants.ScreenPipeName, requireTrustedClientPath: false, OnFrameAsync);

        var tasks = new[]
        {
            _uploader.RunAsync(ct),
            screenServer.RunAsync(ct),
            ControlConnectLoopAsync(ct),
            LiveSocketLoopAsync(ct),
            PolicyPushLoopAsync(ct),
        };
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log.LogInformation("Screen capture worker stopped: {Message}", ex.Message); }
    }

    private async Task OnFrameAsync(ScreenFrameHeader header, byte[] jpeg, CancellationToken ct)
    {
        if (header.Kind == ScreenPipeKind.Periodic)
        {
            _uploader?.Enqueue(header, Guid.NewGuid().ToString("N"), jpeg);
            return;
        }
        var session = CurrentLiveSession;
        if (session != Guid.Empty && _socket is { IsConnected: true })
            await _socket.SendFrameAsync(session, header.Monitor, jpeg, ct).ConfigureAwait(false);
    }

    private async Task LiveSocketLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var id = identity.Current;
                if (id is not null)
                {
                    var socket = new LiveSocketClient(OnLiveControlAsync);
                    await socket.ConnectAsync(api.BaseUrl, id.DeviceId, id.DeviceSecret,
                        (_, cert, _, errors) => trust.Validate(As2(cert), errors), ct).ConfigureAwait(false);
                    _socket = socket;
                    await socket.ReceiveControlLoopAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogDebug("Live socket: {Message}", ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task OnLiveControlAsync(LiveControlMessage msg, CancellationToken ct)
    {
        switch (msg.Action)
        {
            case LiveControlAction.Start:
                CurrentLiveSession = msg.LiveSessionId;
                await SendControlAsync(new ScreenCommand(ScreenCommandKind.LiveStart, null, msg), ct).ConfigureAwait(false);
                break;
            case LiveControlAction.Stop:
                CurrentLiveSession = Guid.Empty;
                await SendControlAsync(new ScreenCommand(ScreenCommandKind.LiveStop, null, msg), ct).ConfigureAwait(false);
                break;
            case LiveControlAction.Switch:
                await SendControlAsync(new ScreenCommand(ScreenCommandKind.LiveSwitch, null, msg), ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task PolicyPushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cfg = await api.GetConfigAsync(ct).ConfigureAwait(false);
                if (cfg.Success && cfg.Value is not null)
                    await SendControlAsync(new ScreenCommand(ScreenCommandKind.SetPolicy, cfg.Value.Policy.ScreenCapture, null), ct).ConfigureAwait(false);
            }
            catch { /* best effort */ }
            try { await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ControlConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_control is null || !_control.IsConnected)
                {
                    var control = new ScreenControlPipeClient();
                    await control.ConnectAsync(ct).ConfigureAwait(false);
                    _control = control;
                }
            }
            catch { /* session not up yet */ }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SendControlAsync(ScreenCommand command, CancellationToken ct)
    {
        try
        {
            if (_control is { IsConnected: true }) await _control.SendAsync(command, ct).ConfigureAwait(false);
        }
        catch { /* pipe dropped; reconnect loop recovers */ }
    }

    private static X509Certificate2? As2(X509Certificate? cert) =>
        cert is null ? null : cert as X509Certificate2 ?? new X509Certificate2(cert);
}
