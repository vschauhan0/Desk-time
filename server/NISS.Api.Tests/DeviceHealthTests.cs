using Microsoft.Extensions.DependencyInjection;
using NISS.Api.Tests.Infrastructure;
using NISS.Application.Health;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

/// <summary>Offline detection with a fake clock: the health worker logic is driven directly through <see cref="IDeviceHealthService"/>.</summary>
[Collection(ApiCollection.Name)]
public sealed class DeviceHealthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<int> RunHealthPassAsync()
    {
        using var scope = factory.Scope();
        return await scope.ServiceProvider.GetRequiredService<IDeviceHealthService>().RunOnceAsync(CancellationToken.None);
    }

    private static async Task<string> StatusAsync(HttpClient admin, TestDevice d) =>
        (await admin.GetJsonAsync<DeviceDetail>($"/api/v1/devices/{d.DeviceId}")).Summary.Status;

    private static async Task<List<SecurityEventDto>> EventsAsync(HttpClient admin, TestDevice d) =>
        (await admin.GetJsonAsync<Paged<SecurityEventDto>>($"/api/v1/security-events?device={d.DeviceId}&pageSize=100")).Items.ToList();

    [Fact]
    public async Task Device_goes_online_warning_offline_and_recovers_with_events()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-H1");
        await RunHealthPassAsync();
        Assert.Equal(DeviceHealth.Pending, await StatusAsync(admin, d));   // never heard from

        await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow()));
        Assert.Equal(DeviceHealth.Online, await StatusAsync(admin, d));

        factory.Clock.Advance(TimeSpan.FromSeconds(45));
        await RunHealthPassAsync();
        Assert.Equal(DeviceHealth.Online, await StatusAsync(admin, d));    // <= 60 s

        factory.Clock.Advance(TimeSpan.FromSeconds(30));                   // 75 s since heartbeat
        await RunHealthPassAsync();
        Assert.Equal(DeviceHealth.Warning, await StatusAsync(admin, d));
        Assert.Contains(await EventsAsync(admin, d), e => e.Kind == SecurityEventKinds.HeartbeatDelayed && e.ResolvedAtUtc is null);

        factory.Clock.Advance(TimeSpan.FromMinutes(5));                    // > 5 min
        await RunHealthPassAsync();
        Assert.Equal(DeviceHealth.Offline, await StatusAsync(admin, d));
        var events = await EventsAsync(admin, d);
        Assert.Contains(events, e => e.Kind == SecurityEventKinds.DeviceOffline && e.Severity == "CRITICAL" && e.ResolvedAtUtc is null);
        Assert.Contains(events, e => e.Kind == SecurityEventKinds.HeartbeatDelayed && e.ResolvedAtUtc is not null);

        // A second pass with no change must not duplicate anything.
        Assert.Equal(0, await RunHealthPassAsync());
        Assert.Single(await EventsAsync(admin, d), e => e.Kind == SecurityEventKinds.DeviceOffline);

        await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow()));
        Assert.Equal(DeviceHealth.Online, await StatusAsync(admin, d));
        events = await EventsAsync(admin, d);
        Assert.Contains(events, e => e.Kind == SecurityEventKinds.DeviceRecovered);
        Assert.All(events.Where(e => e.Kind == SecurityEventKinds.DeviceOffline), e => Assert.NotNull(e.ResolvedAtUtc));
    }

    [Fact]
    public async Task Thresholds_are_configurable_at_runtime()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin);
        await admin.PutJsonAsync("/api/v1/settings/health-thresholds", new HealthThresholds(10, 20, 5000, null));
        try
        {
            await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow()));
            factory.Clock.Advance(TimeSpan.FromSeconds(15));
            await RunHealthPassAsync();
            Assert.Equal(DeviceHealth.Warning, await StatusAsync(admin, d));
            factory.Clock.Advance(TimeSpan.FromSeconds(10));
            await RunHealthPassAsync();
            Assert.Equal(DeviceHealth.Offline, await StatusAsync(admin, d));
        }
        finally
        {
            await admin.PutJsonAsync("/api/v1/settings/health-thresholds", new HealthThresholds(60, 300, 5000, null));
        }
    }

    [Fact]
    public async Task Agent_stopped_means_service_stopped_until_the_next_heartbeat()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin);
        var now = factory.Clock.GetUtcNow();
        await d.HeartbeatAsync(d.NewHeartbeat(now));

        factory.Clock.Advance(TimeSpan.FromSeconds(20));
        await d.IngestAsync(d.Event(EventTypes.AgentStopped, factory.Clock.GetUtcNow(), P.Agent("service stopping")));
        Assert.Equal(DeviceHealth.ServiceStopped, await StatusAsync(admin, d));
        Assert.Contains(await EventsAsync(admin, d), e => e.Kind == SecurityEventKinds.ServiceStopped);

        // Even after the offline threshold the state stays SERVICE_STOPPED (it is not "offline", it was stopped on purpose).
        factory.Clock.Advance(TimeSpan.FromMinutes(20));
        admin = await factory.AdminAsync();   // the earlier access token has expired on the fake clock
        await RunHealthPassAsync();
        Assert.Equal(DeviceHealth.ServiceStopped, await StatusAsync(admin, d));

        await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow()));
        Assert.Equal(DeviceHealth.Online, await StatusAsync(admin, d));
        Assert.All((await EventsAsync(admin, d)).Where(e => e.Kind == SecurityEventKinds.ServiceStopped), e => Assert.NotNull(e.ResolvedAtUtc));
    }

    [Fact]
    public async Task A_stale_agent_stopped_event_does_not_override_a_newer_heartbeat()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin);
        var stopAt = factory.Clock.GetUtcNow();
        factory.Clock.Advance(TimeSpan.FromSeconds(30));
        await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow()));

        await d.IngestAsync(d.Event(EventTypes.AgentStopped, stopAt, P.Agent()));   // backlog: the stop happened before the heartbeat
        Assert.Equal(DeviceHealth.Online, await StatusAsync(admin, d));
    }

    [Fact]
    public async Task Disabled_devices_are_not_touched_by_the_worker()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin);
        await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow()));
        await admin.PostAsync($"/api/v1/devices/{d.DeviceId}/disable", null);

        factory.Clock.Advance(TimeSpan.FromHours(1));
        admin = await factory.AdminAsync();
        await RunHealthPassAsync();
        Assert.Equal(DeviceHealth.Disabled, await StatusAsync(admin, d));
        Assert.DoesNotContain(await EventsAsync(admin, d), e => e.Kind == SecurityEventKinds.DeviceOffline);
    }

    [Theory]
    [InlineData(null, 0, DeviceHealth.Pending)]
    [InlineData(0, 0, DeviceHealth.Online)]
    [InlineData(1, 60, DeviceHealth.Online)]
    [InlineData(1, 61, DeviceHealth.Warning)]
    [InlineData(1, 300, DeviceHealth.Warning)]
    [InlineData(1, 301, DeviceHealth.Offline)]
    public void Evaluator_follows_the_documented_table(int? heartbeatAgeMarker, int ageSeconds, string expected)
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset? lastSeen = heartbeatAgeMarker is null ? null : now.AddSeconds(-ageSeconds);
        Assert.Equal(expected, HealthEvaluator.Evaluate(DeviceHealth.Online, lastSeen, null, now, new HealthThresholds(60, 300, 5000, null)));
    }

    [Fact]
    public void Evaluator_service_stopped_and_admin_states_win()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var t = new HealthThresholds(60, 300, 5000, null);
        Assert.Equal(DeviceHealth.ServiceStopped, HealthEvaluator.Evaluate(DeviceHealth.Online, now.AddHours(-1), now.AddMinutes(-30), now, t));
        Assert.Equal(DeviceHealth.Online, HealthEvaluator.Evaluate(DeviceHealth.ServiceStopped, now.AddSeconds(-5), now.AddMinutes(-30), now, t));   // newer heartbeat
        Assert.Equal(DeviceHealth.Disabled, HealthEvaluator.Evaluate(DeviceHealth.Disabled, now, null, now, t));
        Assert.Equal(DeviceHealth.Revoked, HealthEvaluator.Evaluate(DeviceHealth.Revoked, now, null, now, t));
    }

    [Theory]
    [InlineData("1.9.9", "2.0.0", true)]
    [InlineData("2.0.0", "2.0.0", false)]
    [InlineData("2.0.1", "2.0.0", false)]
    [InlineData("1.0.0-beta", "1.0.1", true)]
    [InlineData("garbage", "2.0.0", false)]
    [InlineData("1.0.0", "", false)]
    [InlineData("1.0.0", null, false)]
    public void Version_comparison(string version, string? minimum, bool outdated) =>
        Assert.Equal(outdated, HealthEvaluator.IsOutdated(version, minimum));
}
