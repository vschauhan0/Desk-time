using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NISS.Api.Tests.Infrastructure;
using NISS.Application.Abstractions;
using NISS.Application.Live;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class LiveViewLifecycleTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private sealed class FakeAgent(Guid deviceId) : ILiveAgentChannel
    {
        public Guid DeviceId => deviceId;
        public readonly ConcurrentQueue<LiveControlMessage> Received = new();
        public Task SendControlAsync(LiveControlMessage message, CancellationToken ct) { Received.Enqueue(message); return Task.CompletedTask; }
        public bool Saw(string action, Guid session) => Received.Any(m => m.Action == action && m.LiveSessionId == session);
    }

    private async Task<(HttpClient Admin, string Code, Guid DeviceId, string Token)> SeedAsync(string emp)
    {
        var (admin, auth) = await factory.LoginAsync("admin", ApiFactory.AdminPassword);
        var device = await factory.EnrollDeviceAsync(admin, emp);
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var d = await db.Devices.FirstAsync(x => x.DeviceCode == device.DeviceId);
        return (admin, d.DeviceCode, d.Id, auth.AccessToken);
    }

    private HubConnection Hub(string token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, HubMethods.Route), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions = ProtocolJson.Create())
            .Build();

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!cts.IsCancellationRequested)
        {
            if (condition()) return true;
            await Task.Delay(50, cts.Token);
        }
        return condition();
    }

    [Fact]
    public async Task Second_start_for_same_device_shares_one_session()
    {
        var (admin, code, deviceId, _) = await SeedAsync("EMP-LC1");
        var relay = factory.Services.GetRequiredService<ILiveRelay>();
        var agent = new FakeAgent(deviceId);
        relay.RegisterAgent(agent);
        try
        {
            var first = await (await admin.PostAsync($"/api/v1/devices/{code}/live-view/start", null)).ReadAsync<LiveViewSessionDto>();
            var second = await (await admin.PostAsync($"/api/v1/devices/{code}/live-view/start", null)).ReadAsync<LiveViewSessionDto>();

            Assert.Equal(first.LiveSessionId, second.LiveSessionId);
            Assert.Equal(1, agent.Received.Count(m => m.Action == LiveControlAction.Start));
            await admin.PostAsync($"/api/v1/devices/{code}/live-view/stop?liveSessionId={first.LiveSessionId}", null);
        }
        finally { relay.UnregisterAgent(agent); }
    }

    [Fact]
    public async Task Disconnecting_the_last_watcher_stops_the_session()
    {
        var (admin, code, deviceId, token) = await SeedAsync("EMP-LC2");
        var relay = factory.Services.GetRequiredService<ILiveRelay>();
        var agent = new FakeAgent(deviceId);
        relay.RegisterAgent(agent);
        try
        {
            var dto = await (await admin.PostAsync($"/api/v1/devices/{code}/live-view/start", null)).ReadAsync<LiveViewSessionDto>();
            var conn = Hub(token);
            await conn.StartAsync();
            await conn.InvokeAsync("WatchLive", dto.LiveSessionId.ToString());

            await conn.StopAsync(); // tab close / navigation / crash
            await conn.DisposeAsync();

            Assert.True(await Eventually(() => agent.Saw(LiveControlAction.Stop, dto.LiveSessionId)));
        }
        finally { relay.UnregisterAgent(agent); }
    }

    [Fact]
    public async Task One_watcher_leaving_does_not_stop_a_session_another_is_still_watching()
    {
        var (admin, code, deviceId, token) = await SeedAsync("EMP-LC3");
        var relay = factory.Services.GetRequiredService<ILiveRelay>();
        var agent = new FakeAgent(deviceId);
        relay.RegisterAgent(agent);
        HubConnection? keep = null;
        try
        {
            var dto = await (await admin.PostAsync($"/api/v1/devices/{code}/live-view/start", null)).ReadAsync<LiveViewSessionDto>();
            keep = Hub(token);
            await keep.StartAsync();
            await keep.InvokeAsync("WatchLive", dto.LiveSessionId.ToString());
            var leaving = Hub(token);
            await leaving.StartAsync();
            await leaving.InvokeAsync("WatchLive", dto.LiveSessionId.ToString());

            await leaving.StopAsync();
            await leaving.DisposeAsync();
            await Task.Delay(500);

            Assert.False(agent.Saw(LiveControlAction.Stop, dto.LiveSessionId));
        }
        finally
        {
            relay.UnregisterAgent(agent);
            if (keep is not null) await keep.DisposeAsync();
        }
    }

    [Fact]
    public void Registry_only_reports_ownership_for_the_owning_device()
    {
        var registry = factory.Services.GetRequiredService<LiveSessionRegistry>();
        var deviceA = Guid.NewGuid();
        var session = Guid.NewGuid();
        registry.Add(new LiveSessionInfo(session, deviceA, "PC-A", 0, DateTimeOffset.UtcNow));

        Assert.True(registry.Owns(session, deviceA));
        Assert.False(registry.Owns(session, Guid.NewGuid()));
        registry.Remove(session);
    }
}
