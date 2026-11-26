using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using NISS.Api.Security;
using NISS.Application.Live;
using NISS.Monitor.Protocol;

namespace NISS.Api.Features;

/// <summary>The persistent agent websocket: control messages down, binary JPEG live frames up.</summary>
public static class LiveSocketEndpoint
{
    private const int MaxFrameBytes = 20 * 1024 * 1024;

    public static void MapLiveSocket(this RouteGroupBuilder api)
    {
        api.MapGet("/agent/live-socket", async (HttpContext ctx, ClaimsPrincipal user, ILiveRelay relay, CancellationToken ct) =>
            {
                if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest("WebSocket required.");
                var deviceId = user.GetDeviceId();
                using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                var channel = new WebSocketAgentChannel(deviceId, ws);
                relay.RegisterAgent(channel);
                try { await ReceiveFramesAsync(ws, relay, ct); }
                catch (OperationCanceledException) { }
                catch (WebSocketException) { }
                finally { relay.UnregisterAgent(channel); }
                return Results.Empty;
            })
            .RequireAuthorization(AuthDefaults.DevicePolicy);
    }

    private static async Task ReceiveFramesAsync(WebSocket ws, ILiveRelay relay, CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        using var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                    return;
                }
                ms.Write(buffer, 0, result.Count);
                if (ms.Length > MaxFrameBytes) return;
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Binary || ms.Length < 20) continue;
            var frame = ms.ToArray();
            var liveSessionId = new Guid(frame.AsSpan(0, 16));
            var monitor = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(16, 4));
            var jpeg = frame[20..];
            await relay.PushFrameAsync(liveSessionId, monitor, jpeg, ct);
        }
    }

    private sealed class WebSocketAgentChannel(Guid deviceId, WebSocket ws) : ILiveAgentChannel
    {
        public Guid DeviceId => deviceId;

        public Task SendControlAsync(LiveControlMessage message, CancellationToken ct)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Options);
            return ws.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
    }
}
