using System.Buffers.Binary;
using System.Net.Security;
using System.Net.WebSockets;
using System.Text.Json;
using NISS.Monitor.Protocol;

namespace NISS.Monitor.Infrastructure.Live;

/// <summary>
/// Service-side persistent websocket to the server for live view: receives <see cref="LiveControlMessage"/> control
/// (start/stop/switch) and sends binary JPEG frames up, each tagged with its live-session id.
/// </summary>
public sealed class LiveSocketClient(Func<LiveControlMessage, CancellationToken, Task> onControl) : IAsyncDisposable
{
    private ClientWebSocket? _ws;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public async Task ConnectAsync(string baseUrl, string deviceId, string secret, RemoteCertificateValidationCallback? pin, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"NissDevice {deviceId}:{secret}");
        if (pin is not null) ws.Options.RemoteCertificateValidationCallback = pin;
        var url = baseUrl.TrimEnd('/').Replace("https://", "wss://").Replace("http://", "ws://") + "/api/v1/agent/live-socket";
        await ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
        _ws = ws;
    }

    public async Task SendFrameAsync(Guid liveSessionId, int monitor, byte[] jpeg, CancellationToken ct)
    {
        if (_ws is null || _ws.State != WebSocketState.Open) return;
        var buf = new byte[20 + jpeg.Length];
        liveSessionId.ToByteArray().CopyTo(buf.AsSpan(0, 16));
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(16, 4), monitor);
        jpeg.CopyTo(buf.AsSpan(20));
        await _ws.SendAsync(buf, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
    }

    public async Task ReceiveControlLoopAsync(CancellationToken ct)
    {
        if (_ws is null) return;
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;
            var msg = JsonSerializer.Deserialize<LiveControlMessage>(ms.ToArray(), ProtocolJson.Options);
            if (msg is not null) await onControl(msg, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws is null) return;
        try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); }
        catch { /* ignore */ }
        _ws.Dispose();
    }
}
