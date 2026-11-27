using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NISS.Api.Tests.Infrastructure;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Application.Updates;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

/// <summary>Rollout lifecycle endpoints and the canary health gate that unlocks "roll out to all".</summary>
[Collection(ApiCollection.Name)]
public sealed class RolloutLifecycleTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string NewVersion() => $"{Random.Shared.Next(100, 900)}.{Random.Shared.Next(0, 999)}.{Random.Shared.Next(0, 999)}.0";

    private async Task<Device> NewDeviceAsync(string agentVersion = "1.0.0.0")
    {
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var now = factory.Clock.GetUtcNow();
        var device = new Device
        {
            Id = Guid.NewGuid(), DeviceCode = "RL-" + Guid.NewGuid().ToString("N")[..8], Hostname = "rl-host",
            AgentVersion = agentVersion, Status = "ONLINE", CreatedAt = now, UpdatedAt = now,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device;
    }

    private async Task AddPackageAsync(string version)
    {
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        if (await db.AgentPackages.AnyAsync(p => p.Version == version)) return;
        db.AgentPackages.Add(new AgentPackage { Id = Guid.NewGuid(), Version = version, RelPath = $"{version}.zip", Sha256 = new string('a', 64), UploadedAt = factory.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
    }

    /// <summary>Creates a rollout through the API and puts the given devices in its canary set.</summary>
    private async Task<AgentRolloutDto> CanaryAsync(HttpClient admin, string version, params Device[] devices)
    {
        await AddPackageAsync(version);
        var created = await admin.PostJsonAsync("/api/v1/agent-rollouts", new CreateRolloutRequest(version, "n"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await created.ReadAsync<AgentRolloutDto>();
        Assert.Equal("Draft", dto.State);
        var set = await admin.PostJsonAsync($"/api/v1/agent-rollouts/{dto.Id}/canary", new CanaryRequest(devices.Select(d => d.Id.ToString()).ToList()));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        return await set.ReadAsync<AgentRolloutDto>();
    }

    private static readonly CollectorStatus[] Healthy = [new("process", "Running", null, null), new("network", "Running", null, null)];
    private static readonly CollectorStatus[] Broken = [new("process", "Running", null, null), new("network", "Faulted", "boom", null)];

    /// <summary>The device reported a successful update to <paramref name="version"/> and is alive with the given collectors.</summary>
    private async Task SeedUpdatedAsync(Device device, string version, CollectorStatus[] collectors)
    {
        using (var scope = factory.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var state = await db.DeviceUpdateStates.FirstOrDefaultAsync(s => s.DeviceId == device.Id);
            if (state is null) db.DeviceUpdateStates.Add(state = new DeviceUpdateState { Id = Guid.NewGuid(), DeviceId = device.Id });
            state.AssignedVersion = version; state.Phase = UpdatePhase.Succeeded; state.UpdatedAt = factory.Clock.GetUtcNow();
            await db.SaveChangesAsync();
        }
        await SeedStatusAsync(device, collectors);
    }

    private async Task SeedStatusAsync(Device device, CollectorStatus[] collectors)
    {
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var now = factory.Clock.GetUtcNow();
        var status = await db.AgentStatuses.FirstOrDefaultAsync(s => s.DeviceId == device.Id);
        if (status is null) db.AgentStatuses.Add(status = new AgentStatus { DeviceId = device.Id });
        status.ServiceState = "Running"; status.CollectorsJson = JsonColumn.Serialize(collectors);
        status.LastHeartbeatAt = now; status.LiveUpdatedAt = now; status.UpdatedAt = now;
        await db.SaveChangesAsync();
    }

    private async Task SeedFailedAsync(Device device, string version)
    {
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        db.DeviceUpdateStates.Add(new DeviceUpdateState { Id = Guid.NewGuid(), DeviceId = device.Id, AssignedVersion = version, Phase = UpdatePhase.RolledBack, FailedVersion = version, UpdatedAt = factory.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> Promote(HttpClient c, string id) => c.PostAsync($"/api/v1/agent-rollouts/{id}/rollout", null);

    [Fact]
    public async Task Rollout_is_refused_until_canary_is_healthy_and_baked_then_succeeds()
    {
        var admin = await factory.AdminAsync();
        var v = NewVersion();
        var d = await NewDeviceAsync();
        var rollout = await CanaryAsync(admin, v, d);
        Assert.Equal("Canary", rollout.State);

        Assert.Equal(HttpStatusCode.Conflict, (await Promote(admin, rollout.Id)).StatusCode);   // nothing attempted yet

        await SeedUpdatedAsync(d, v, Healthy);
        Assert.Equal(HttpStatusCode.Conflict, (await Promote(admin, rollout.Id)).StatusCode);   // healthy but not baked

        factory.Clock.Advance(TimeSpan.FromHours(25));
        admin = await factory.AdminAsync();   // a day on the fake clock expires the access token
        await SeedStatusAsync(d, Broken);
        Assert.Equal(HttpStatusCode.Conflict, (await Promote(admin, rollout.Id)).StatusCode);   // baked but a collector is down

        await SeedStatusAsync(d, Healthy);
        var ok = await Promote(admin, rollout.Id);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("RolledOut", (await ok.ReadAsync<AgentRolloutDto>()).State);
    }

    [Fact]
    public async Task Offline_or_never_seen_canary_devices_do_not_block_the_gate()
    {
        var admin = await factory.AdminAsync();
        var v = NewVersion();
        var good = await NewDeviceAsync();
        var offline = await NewDeviceAsync();        // never attempted, never seen
        var stale = await NewDeviceAsync();          // updated, then went offline: status is old
        var rollout = await CanaryAsync(admin, v, good, offline, stale);

        await SeedUpdatedAsync(stale, v, Healthy);
        factory.Clock.Advance(TimeSpan.FromHours(25));   // stale's status is now a day old
        admin = await factory.AdminAsync();   // a day on the fake clock expires the access token
        await SeedUpdatedAsync(good, v, Healthy);
        factory.Clock.Advance(TimeSpan.FromHours(25));
        admin = await factory.AdminAsync();   // a day on the fake clock expires the access token
        await SeedStatusAsync(good, Healthy);

        var dto = await admin.GetJsonAsync<AgentRolloutDto>($"/api/v1/agent-rollouts/{rollout.Id}");
        Assert.Equal(3, dto.Canary);
        Assert.Equal(1, dto.Updated);
        Assert.Equal(0, dto.Failed);
        Assert.Equal(2, dto.Pending);

        Assert.Equal(HttpStatusCode.OK, (await Promote(admin, rollout.Id)).StatusCode);
    }

    [Fact]
    public async Task An_unresolved_canary_failure_blocks_the_gate_and_rollback_transitions_state()
    {
        var admin = await factory.AdminAsync();
        var v = NewVersion();
        var good = await NewDeviceAsync();
        var bad = await NewDeviceAsync();
        var rollout = await CanaryAsync(admin, v, good, bad);
        await SeedUpdatedAsync(good, v, Healthy);
        await SeedFailedAsync(bad, v);
        factory.Clock.Advance(TimeSpan.FromHours(25));
        admin = await factory.AdminAsync();   // a day on the fake clock expires the access token
        await SeedStatusAsync(good, Healthy);

        Assert.Equal(HttpStatusCode.Conflict, (await Promote(admin, rollout.Id)).StatusCode);
        var dto = await admin.GetJsonAsync<AgentRolloutDto>($"/api/v1/agent-rollouts/{rollout.Id}");
        Assert.Equal((1, 1, 0), (dto.Updated, dto.Failed, dto.Pending));

        var rb = await admin.PostAsync($"/api/v1/agent-rollouts/{rollout.Id}/rollback", null);
        Assert.Equal(HttpStatusCode.OK, rb.StatusCode);
        Assert.Equal("RolledBack", (await rb.ReadAsync<AgentRolloutDto>()).State);
        Assert.Equal(HttpStatusCode.Conflict, (await Promote(admin, rollout.Id)).StatusCode);   // a rolled-back rollout cannot be promoted
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/agent-rollouts/{rollout.Id}/rollback", null)).StatusCode);

        // Quarantine stays coherent: the failed device keeps its FailedVersion and nobody is targeted by a RolledBack rollout.
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        Assert.Equal(v, (await db.DeviceUpdateStates.SingleAsync(s => s.DeviceId == bad.Id)).FailedVersion);
        var updates = scope.ServiceProvider.GetRequiredService<IAgentUpdateService>();
        Assert.Null(await updates.TargetVersionForAsync(good, "1.0.0.0", default));
    }

    [Fact]
    public async Task Pause_stops_targeting()
    {
        var admin = await factory.AdminAsync();
        var v = NewVersion();
        var d = await NewDeviceAsync();
        var rollout = await CanaryAsync(admin, v, d);
        var paused = await admin.PostAsync($"/api/v1/agent-rollouts/{rollout.Id}/pause", null);
        Assert.Equal("Paused", (await paused.ReadAsync<AgentRolloutDto>()).State);
        using var scope = factory.Scope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IAgentUpdateService>().TargetVersionForAsync(d, "1.0.0.0", default));
    }

    [Fact]
    public async Task Viewer_is_403_on_every_rollout_endpoint()
    {
        var superAdmin = await factory.AdminAsync();
        var client = await factory.UserWithRoleAsync(superAdmin, "VIEWER");
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostJsonAsync("/api/v1/agent-rollouts", new CreateRolloutRequest("1.2.3.4", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostJsonAsync($"/api/v1/agent-rollouts/{id}/canary", new CanaryRequest([]))).StatusCode);
        foreach (var action in new[] { "rollout", "pause", "rollback" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/v1/agent-rollouts/{id}/{action}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/agent-rollouts/{id}")).StatusCode);
    }

    [Fact]
    public async Task Plain_admin_can_run_the_lifecycle_and_each_mutation_is_audited()
    {
        var superAdmin = await factory.AdminAsync();
        var admin = await factory.UserWithRoleAsync(superAdmin, "ADMIN");
        var v = NewVersion();
        var d = await NewDeviceAsync();
        var rollout = await CanaryAsync(admin, v, d);
        await SeedUpdatedAsync(d, v, Healthy);
        factory.Clock.Advance(TimeSpan.FromHours(25));
        admin = await factory.UserWithRoleAsync(await factory.AdminAsync(), "ADMIN");   // token expired on the fake clock; log in as a fresh ADMIN
        await SeedStatusAsync(d, Healthy);
        Assert.Equal(HttpStatusCode.OK, (await Promote(admin, rollout.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/agent-rollouts/{rollout.Id}/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/agent-rollouts/{rollout.Id}/rollback", null)).StatusCode);

        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var actions = await db.AuditLogs.Where(a => a.Target == v).Select(a => a.Action).ToListAsync();
        foreach (var expected in new[] { "ROLLOUT_CREATED", "ROLLOUT_CANARY_SET", "ROLLOUT_PROMOTED", "ROLLOUT_PAUSED", "ROLLOUT_ROLLED_BACK" })
            Assert.Contains(expected, actions);
    }

    [Fact]
    public async Task A_device_that_recovered_past_its_FailedVersion_has_it_cleared_on_heartbeat()
    {
        var admin = await factory.AdminAsync();
        var d = await factory.EnrollDeviceAsync(admin, "EMP-RL1");
        var failed = "2.0.0.0";
        using (var scope = factory.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var id = await db.Devices.Where(x => x.DeviceCode == d.DeviceId).Select(x => x.Id).SingleAsync();
            db.DeviceUpdateStates.Add(new DeviceUpdateState { Id = Guid.NewGuid(), DeviceId = id, AssignedVersion = failed, Phase = UpdatePhase.RolledBack, FailedVersion = failed, UpdatedAt = factory.Clock.GetUtcNow() });
            await db.SaveChangesAsync();
        }

        await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow(), version: "1.5.0.0"));   // still below: stays quarantined
        Assert.Equal(failed, await FailedVersionAsync(d.DeviceId));

        await d.HeartbeatAsync(d.NewHeartbeat(factory.Clock.GetUtcNow(), version: "2.1.0.0"));   // moved past it
        Assert.Null(await FailedVersionAsync(d.DeviceId));
    }

    private async Task<string?> FailedVersionAsync(string code)
    {
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var id = await db.Devices.Where(x => x.DeviceCode == code).Select(x => x.Id).SingleAsync();
        return (await db.DeviceUpdateStates.AsNoTracking().SingleAsync(s => s.DeviceId == id)).FailedVersion;
    }
}
