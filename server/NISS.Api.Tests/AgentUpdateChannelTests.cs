using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NISS.Api.Tests.Infrastructure;
using NISS.Application.Abstractions;
using NISS.Application.Updates;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

/// <summary>Device-facing update channel: heartbeat target, package download (own target only), and status reports.</summary>
[Collection(ApiCollection.Name)]
public sealed class AgentUpdateChannelTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string NewVersion() => $"{Random.Shared.Next(100, 900)}.{Random.Shared.Next(0, 999)}.{Random.Shared.Next(0, 999)}.0";

    /// <summary>Makes <paramref name="version"/> the only live (RolledOut) rollout, with a stored package.</summary>
    private async Task StartRolloutAsync(string version)
    {
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        foreach (var live in await db.AgentRollouts.Where(r => r.State == RolloutState.Canary || r.State == RolloutState.RolledOut).ToListAsync())
            live.State = RolloutState.Done;
        var rel = await scope.ServiceProvider.GetRequiredService<IPackageStore>().SaveAsync(version, [1, 2, 3, 4], "{}"u8.ToArray(), default);
        db.AgentPackages.Add(new AgentPackage { Id = Guid.NewGuid(), Version = version, RelPath = rel, SizeBytes = 4, Sha256 = new string('a', 64), UploadedAt = factory.Clock.GetUtcNow() });
        var now = factory.Clock.GetUtcNow();
        db.AgentRollouts.Add(new AgentRollout { Id = Guid.NewGuid(), Version = version, State = RolloutState.RolledOut, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
    }

    private async Task<DeviceUpdateState?> StateAsync(string deviceCode)
    {
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var id = await db.Devices.Where(d => d.DeviceCode == deviceCode).Select(d => d.Id).SingleAsync();
        return await db.DeviceUpdateStates.AsNoTracking().SingleOrDefaultAsync(s => s.DeviceId == id);
    }

    [Fact]
    public async Task Assigned_device_heartbeat_returns_target_version_and_url()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-UC1");
        var v = NewVersion();
        await StartRolloutAsync(v);

        var hb = await (await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow(), version: "1.0.0.0"))).ReadAsync<HeartbeatResponse>();

        Assert.Equal(v, hb.TargetAgentVersion);
        Assert.Equal($"/api/v1/agent/update/{v}", hb.UpdateUrl);
    }

    [Fact]
    public async Task Up_to_date_device_gets_no_target()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-UC2");
        var v = NewVersion();
        await StartRolloutAsync(v);

        var hb = await (await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow(), version: v))).ReadAsync<HeartbeatResponse>();

        Assert.Null(hb.TargetAgentVersion);
        Assert.Null(hb.UpdateUrl);
    }

    [Fact]
    public async Task Download_of_assigned_version_streams_the_package_and_other_versions_are_forbidden()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-UC3");
        var v = NewVersion();
        await StartRolloutAsync(v);
        await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow(), version: "1.0.0.0"));

        var ok = await d.Client.GetAsync($"/api/v1/agent/update/{v}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal([1, 2, 3, 4], await ok.Content.ReadAsByteArrayAsync());

        var other = await d.Client.GetAsync($"/api/v1/agent/update/{NewVersion()}");
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    }

    [Fact]
    public async Task Download_of_a_version_not_assigned_to_the_device_is_forbidden()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-UC4");
        var v = NewVersion();
        await StartRolloutAsync(v);
        await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow(), version: v));   // already on it: nothing assigned

        Assert.Equal(HttpStatusCode.Forbidden, (await d.Client.GetAsync($"/api/v1/agent/update/{v}")).StatusCode);
    }

    [Fact]
    public async Task Rolled_back_report_sets_failed_version_and_raises_update_failed()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-UC5");
        var v = NewVersion();
        await StartRolloutAsync(v);

        var resp = await d.Client.PostJsonAsync("/api/v1/agent/update/status", new UpdateStatusReport(v, "rolled_back", "health check failed", 2));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        var state = await StateAsync(d.DeviceId);
        Assert.NotNull(state);
        Assert.Equal(v, state!.FailedVersion);
        Assert.Equal(UpdatePhase.RolledBack, state.Phase);
        Assert.Equal(2, state.Attempts);
        Assert.Equal("health check failed", state.LastError);

        var events = (await admin.GetJsonAsync<Paged<SecurityEventDto>>($"/api/v1/security-events?device={d.DeviceId}&pageSize=100")).Items;
        var ev = Assert.Single(events, e => e.Kind == SecurityEventKinds.UpdateFailed);
        Assert.Equal("WARNING", ev.Severity);

        // Quarantine: the next heartbeat no longer offers that version.
        var hb = await (await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow(), version: "1.0.0.0"))).ReadAsync<HeartbeatResponse>();
        Assert.Null(hb.TargetAgentVersion);
    }

    [Fact]
    public async Task Progress_report_updates_phase_without_failing()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-UC6");
        var v = NewVersion();

        await d.Client.PostJsonAsync("/api/v1/agent/update/status", new UpdateStatusReport(v, "downloading", null, 1));
        await d.Client.PostJsonAsync("/api/v1/agent/update/status", new UpdateStatusReport(v, "installing", null, 1));

        var state = await StateAsync(d.DeviceId);
        Assert.Equal(UpdatePhase.Applying, state!.Phase);
        Assert.Equal(v, state.AssignedVersion);
        Assert.Null(state.FailedVersion);
    }
}
