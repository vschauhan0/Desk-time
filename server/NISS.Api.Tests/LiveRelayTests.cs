using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using NISS.Api.Realtime;
using NISS.Api.Tests.Infrastructure;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class LiveRelayTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(HubConnection Conn, Channel<ScreenFrameMessage> Frames)> WatchAsync(string token, Guid session)
    {
        var frames = Channel.CreateUnbounded<ScreenFrameMessage>();
        var conn = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, HubMethods.Route), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions = ProtocolJson.Create())
            .Build();
        conn.On<ScreenFrameMessage>(HubMethods.ScreenFrame, m => frames.Writer.TryWrite(m));
        await conn.StartAsync();
        await conn.InvokeAsync("WatchLive", session.ToString());
        return (conn, frames);
    }

    [Fact]
    public async Task Relay_forwards_frames_to_the_watching_group_and_isolates_sessions()
    {
        var (_, auth) = await factory.LoginAsync("admin", ApiFactory.AdminPassword);
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        var (connA, framesA) = await WatchAsync(auth.AccessToken, sessionA);
        var (connB, framesB) = await WatchAsync(auth.AccessToken, sessionB);
        await using var _a = connA;
        await using var _b = connB;
        var relay = factory.Services.GetRequiredService<ILiveRelay>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await relay.PushFrameAsync(sessionA, 0, new byte[] { 1, 2, 3 }, default);
        var gotA = await framesA.Reader.ReadAsync(cts.Token);
        await relay.PushFrameAsync(sessionB, 1, new byte[] { 4, 5 }, default);
        var gotB = await framesB.Reader.ReadAsync(cts.Token);

        Assert.Equal(sessionA, gotA.LiveSessionId);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), gotA.JpegBase64);
        Assert.Equal(sessionB, gotB.LiveSessionId);
        Assert.False(framesA.Reader.TryRead(out _)); // watcher A never received device B's frame
    }

    [Fact]
    public async Task LiveSocket_requires_device_auth()
    {
        var wsClient = factory.Server.CreateWebSocketClient();
        var uri = new Uri(factory.Server.BaseAddress, "/api/v1/agent/live-socket");

        await Assert.ThrowsAnyAsync<Exception>(() => wsClient.ConnectAsync(uri, default));
    }
}
