using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NISS.Api.Tests.Infrastructure;
using NISS.Application.Abstractions;
using NISS.Application.Live;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class LiveViewServiceTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private sealed class FakeAgent(Guid deviceId) : ILiveAgentChannel
    {
        public Guid DeviceId => deviceId;
        public readonly List<LiveControlMessage> Received = new();
        public Task SendControlAsync(LiveControlMessage message, CancellationToken ct) { Received.Add(message); return Task.CompletedTask; }
    }

    private async Task<(HttpClient Admin, string Code, Guid DeviceId)> SeedAsync(string emp)
    {
        var admin = await factory.AdminAsync();
        var device = await factory.EnrollDeviceAsync(admin, emp);
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var d = await db.Devices.FirstAsync(x => x.DeviceCode == device.DeviceId);
        return (admin, d.DeviceCode, d.Id);
    }

    [Fact]
    public async Task Start_rejected_when_agent_not_connected()
    {
        var (admin, code, _) = await SeedAsync("EMP-LV1");

        var resp = await admin.PostAsync($"/api/v1/devices/{code}/live-view/start", null);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Start_sends_start_control_and_audits()
    {
        var (admin, code, deviceId) = await SeedAsync("EMP-LV2");
        var relay = factory.Services.GetRequiredService<ILiveRelay>();
        var agent = new FakeAgent(deviceId);
        relay.RegisterAgent(agent);
        try
        {
            var dto = await (await admin.PostAsync($"/api/v1/devices/{code}/live-view/start?monitor=0", null)).ReadAsync<LiveViewSessionDto>();

            Assert.Contains(agent.Received, m => m.Action == LiveControlAction.Start && m.LiveSessionId == dto.LiveSessionId);
            var logs = await admin.GetJsonAsync<Paged<AuditLogDto>>("/api/v1/audit-logs?pageSize=500&action=LIVE_VIEW_STARTED");
            Assert.Contains(logs.Items, l => l.Target == code);
        }
        finally { relay.UnregisterAgent(agent); }
    }

    [Fact]
    public async Task Stop_sends_stop_control_and_audits()
    {
        var (admin, code, deviceId) = await SeedAsync("EMP-LV3");
        var relay = factory.Services.GetRequiredService<ILiveRelay>();
        var agent = new FakeAgent(deviceId);
        relay.RegisterAgent(agent);
        try
        {
            var dto = await (await admin.PostAsync($"/api/v1/devices/{code}/live-view/start", null)).ReadAsync<LiveViewSessionDto>();

            var stop = await admin.PostAsync($"/api/v1/devices/{code}/live-view/stop?liveSessionId={dto.LiveSessionId}", null);

            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
            Assert.Contains(agent.Received, m => m.Action == LiveControlAction.Stop && m.LiveSessionId == dto.LiveSessionId);
            var logs = await admin.GetJsonAsync<Paged<AuditLogDto>>("/api/v1/audit-logs?pageSize=500&action=LIVE_VIEW_ENDED");
            Assert.Contains(logs.Items, l => l.Target == code);
        }
        finally { relay.UnregisterAgent(agent); }
    }

    [Fact]
    public async Task Start_rejected_when_live_disabled_by_policy()
    {
        var (admin, code, deviceId) = await SeedAsync("EMP-LV4");
        var relay = factory.Services.GetRequiredService<ILiveRelay>();
        var agent = new FakeAgent(deviceId);
        relay.RegisterAgent(agent);
        var global = (await admin.GetJsonAsync<List<PolicyDto>>("/api/v1/settings/policies")).Single(p => p.Scope == "GLOBAL");
        try
        {
            var disabled = global.Policy with { ScreenCapture = global.Policy.ScreenCapture with { LiveEnabled = false } };
            await admin.PutJsonAsync($"/api/v1/settings/policies/{global.Id}", new PolicyUpsert(global.Name, "GLOBAL", null, disabled));

            var resp = await admin.PostAsync($"/api/v1/devices/{code}/live-view/start", null);

            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        }
        finally
        {
            var restored = global.Policy with { ScreenCapture = global.Policy.ScreenCapture with { LiveEnabled = true } };
            await admin.PutJsonAsync($"/api/v1/settings/policies/{global.Id}", new PolicyUpsert(global.Name, "GLOBAL", null, restored));
            relay.UnregisterAgent(agent);
        }
    }
}
