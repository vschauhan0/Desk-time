using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NISS.Api.Tests.Infrastructure;
using NISS.Application.Abstractions;
using NISS.Application.Updates;
using NISS.Domain;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

/// <summary>
/// Layer-3 tamper alerting: a deliberate stop while a user is signed in becomes a Critical, attributed alert; a
/// benign shutdown stays the quiet ServiceStopped. See docs/superpowers/specs/2026-11-26-tamper-resistance-design.md.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TamperAlertTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<List<SecurityEventDto>> EventsAsync(HttpClient admin, TestDevice d) =>
        (await admin.GetJsonAsync<Paged<SecurityEventDto>>($"/api/v1/security-events?device={d.DeviceId}&pageSize=100")).Items.ToList();

    [Fact]
    public async Task Deliberate_stop_while_a_user_is_signed_in_raises_a_critical_attributed_alert()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-TMP1");
        await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow()));
        factory.Clock.Advance(TimeSpan.FromSeconds(20));

        await d.IngestAsync(d.Event(EventTypes.AgentStopped, factory.Clock.GetUtcNow(),
            P.Agent(message: "stopped", interactiveUser: "CONTOSO\\jdoe")));

        var events = await EventsAsync(admin, d);
        var tamper = Assert.Single(events, e => e.Kind == SecurityEventKinds.TamperSuspected);
        Assert.Equal("CRITICAL", tamper.Severity);
        Assert.Null(tamper.ResolvedAtUtc);
        Assert.Contains("jdoe", tamper.Message);
        // The device is still shown as service-stopped (the health state is orthogonal to the tamper alert).
        Assert.Equal(DeviceHealth.ServiceStopped, (await admin.GetJsonAsync<DeviceDetail>($"/api/v1/devices/{d.DeviceId}")).Summary.Status);
    }

    [Fact]
    public async Task Benign_shutdown_does_not_raise_a_tamper_alert()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-TMP2");
        await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow()));
        factory.Clock.Advance(TimeSpan.FromSeconds(20));

        await d.IngestAsync(d.Event(EventTypes.AgentStopped, factory.Clock.GetUtcNow(), P.Agent())); // no interactive user => benign

        var events = await EventsAsync(admin, d);
        Assert.DoesNotContain(events, e => e.Kind == SecurityEventKinds.TamperSuspected);
        Assert.Contains(events, e => e.Kind == SecurityEventKinds.ServiceStopped);
    }

    private const string User = @"CONTOSO\jdoe";

    /// <summary>Ends every live rollout so one test's wave cannot leak into another's.</summary>
    private async Task EndRolloutsAsync()
    {
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        foreach (var live in await db.AgentRollouts.Where(r => r.State == RolloutState.Canary || r.State == RolloutState.RolledOut).ToListAsync())
            live.State = RolloutState.Done;
        await db.SaveChangesAsync();
    }

    /// <summary>The server's own rollout targets the device and the device downloads its package: this is what opens the restart window.</summary>
    private async Task<string> ServePackageAsync(TestDevice d)
    {
        await EndRolloutsAsync();
        factory.Clock.Advance(TimeSpan.FromMinutes(1));   // newest live rollout wins
        var version = $"{Random.Shared.Next(100, 900)}.{Random.Shared.Next(0, 999)}.0.0";
        using (var scope = factory.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var rel = await scope.ServiceProvider.GetRequiredService<IPackageStore>().SaveAsync(version, [1, 2, 3, 4], "{}"u8.ToArray(), default);
            var now = factory.Clock.GetUtcNow();
            db.AgentPackages.Add(new AgentPackage { Id = Guid.NewGuid(), Version = version, RelPath = rel, SizeBytes = 4, Sha256 = new string('a', 64), UploadedAt = now });
            db.AgentRollouts.Add(new AgentRollout { Id = Guid.NewGuid(), Version = version, State = RolloutState.RolledOut, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }
        await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow(), version: "1.0.0.0"));
        Assert.Equal(System.Net.HttpStatusCode.OK, (await d.Client.GetAsync($"/api/v1/agent/update/{version}")).StatusCode);
        return version;
    }

    private async Task<List<SecurityEventDto>> DeliberateStopAsync(HttpClient admin, TestDevice d, string? reason = null)
    {
        factory.Clock.Advance(TimeSpan.FromSeconds(20));
        await d.IngestAsync(d.Event(EventTypes.AgentStopped, factory.Clock.GetUtcNow(), P.Agent(message: reason, interactiveUser: User)));
        return await EventsAsync(admin, d);
    }

    [Fact]
    public async Task Stop_shortly_after_the_server_served_the_package_is_an_update_restart_not_tamper()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-TMP3");
        await ServePackageAsync(d);

        var events = await DeliberateStopAsync(admin, d, StopReasons.UpdateRestart);

        Assert.DoesNotContain(events, e => e.Kind == SecurityEventKinds.TamperSuspected);
        Assert.Contains(events, e => e.Kind == SecurityEventKinds.ServiceStopped);
        await EndRolloutsAsync();
    }

    [Fact]
    public async Task Stop_after_the_restart_window_closes_raises_tamper_again()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-TMP6");
        await ServePackageAsync(d);
        factory.Clock.Advance(TimeSpan.FromMinutes(31));   // past the 30-minute default window
        await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow(), version: "1.0.0.0"));

        admin = await factory.AdminAsync();   // the admin token expires with the advanced clock
        var tamper = Assert.Single(await DeliberateStopAsync(admin, d), e => e.Kind == SecurityEventKinds.TamperSuspected);
        Assert.Equal("CRITICAL", tamper.Severity);
        await EndRolloutsAsync();
    }

    [Fact]
    public async Task Redownloading_the_same_version_does_not_extend_the_restart_window()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-TMP7");
        var version = await ServePackageAsync(d);
        for (var i = 0; i < 2; i++)   // the agent keeps re-fetching its target to try to hold the window open
        {
            factory.Clock.Advance(TimeSpan.FromMinutes(20));
            await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow(), version: "1.0.0.0"));
            Assert.Equal(System.Net.HttpStatusCode.OK, (await d.Client.GetAsync($"/api/v1/agent/update/{version}")).StatusCode);
        }
        admin = await factory.AdminAsync();   // the admin token expires with the advanced clock

        var tamper = Assert.Single(await DeliberateStopAsync(admin, d), e => e.Kind == SecurityEventKinds.TamperSuspected);   // 40 min after the first serve
        Assert.Equal("CRITICAL", tamper.Severity);
        await EndRolloutsAsync();
    }

    [Fact]
    public async Task Forged_update_hint_without_a_server_served_package_still_raises_tamper()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-TMP4");
        await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow()));

        // The agent claims an update restart, but the server served it nothing: the hint must not suppress the alert.
        var tamper = Assert.Single(await DeliberateStopAsync(admin, d, StopReasons.UpdateRestart), e => e.Kind == SecurityEventKinds.TamperSuspected);
        Assert.Equal("CRITICAL", tamper.Severity);
    }

    [Fact]
    public async Task Forged_status_and_a_low_version_claim_without_a_server_served_package_still_raise_tamper()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-TMP5");
        await EndRolloutsAsync();
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        using (var scope = factory.Scope())   // a live RolledOut wave covers every device, yet the server never served this one
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var now = factory.Clock.GetUtcNow();
            db.AgentRollouts.Add(new AgentRollout { Id = Guid.NewGuid(), Version = "998.0.0.0", State = RolloutState.RolledOut, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }
        await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow(), version: "0.0.1"));   // under-reported version
        await d.Client.PostJsonAsync("/api/v1/agent/update/status", new UpdateStatusReport("998.0.0.0", "downloading", null, 1));   // forged progress

        var tamper = Assert.Single(await DeliberateStopAsync(admin, d, StopReasons.UpdateRestart), e => e.Kind == SecurityEventKinds.TamperSuspected);
        Assert.Equal("CRITICAL", tamper.Severity);
        await EndRolloutsAsync();
    }
}
