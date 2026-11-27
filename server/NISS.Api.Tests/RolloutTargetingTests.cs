using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NISS.Api.Tests.Infrastructure;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Application.Settings;
using NISS.Application.Updates;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Api.Tests;

/// <summary>Rollout wave membership, the MinAgentVersion floor, and the failed-version quarantine.</summary>
[Collection(ApiCollection.Name)]
public sealed class RolloutTargetingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string NewVersion() => $"{Random.Shared.Next(100, 900)}.{Random.Shared.Next(0, 999)}.{Random.Shared.Next(0, 999)}.0";

    private async Task<Device> NewDeviceAsync(string agentVersion = "1.0.0.0")
    {
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var now = factory.Clock.GetUtcNow();
        var device = new Device
        {
            Id = Guid.NewGuid(), DeviceCode = "RT-" + Guid.NewGuid().ToString("N")[..8], Hostname = "rt-host",
            AgentVersion = agentVersion, Status = "ONLINE", CreatedAt = now, UpdatedAt = now,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device;
    }

    private async Task<AgentRollout> NewRolloutAsync(string version)
    {
        factory.Clock.Advance(TimeSpan.FromMinutes(1));   // "newest active rollout wins" needs distinct timestamps
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        foreach (var live in await db.AgentRollouts.Where(r => r.State == RolloutState.Canary || r.State == RolloutState.RolledOut).ToListAsync())
            live.State = RolloutState.Done;   // isolate from rollouts left by other tests
        if (!await db.AgentPackages.AnyAsync(p => p.Version == version))
        {
            db.AgentPackages.Add(new AgentPackage { Id = Guid.NewGuid(), Version = version, RelPath = $"{version}.zip", Sha256 = new string('a', 64), UploadedAt = factory.Clock.GetUtcNow() });
            await db.SaveChangesAsync();
        }
        return await scope.ServiceProvider.GetRequiredService<IAgentUpdateService>().CreateRolloutAsync(version, "notes", "tester", default);
    }

    private async Task SetCanaryAsync(Guid rolloutId, params Guid[] devices)
    {
        using var scope = factory.Scope();
        await scope.ServiceProvider.GetRequiredService<IAgentUpdateService>().SetCanaryAsync(rolloutId, devices, "tester", default);
    }

    private async Task<string?> TargetAsync(Device device)
    {
        using var scope = factory.Scope();
        return await scope.ServiceProvider.GetRequiredService<IAgentUpdateService>().TargetVersionForAsync(device, device.AgentVersion, default);
    }

    private async Task SetRolloutStateAsync(Guid id, string state)
    {
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        (await db.AgentRollouts.SingleAsync(r => r.Id == id)).State = state;
        await db.SaveChangesAsync();
    }

    private async Task SetFailedVersionAsync(Guid deviceId, string failedVersion)
    {
        using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        db.DeviceUpdateStates.Add(new DeviceUpdateState { Id = Guid.NewGuid(), DeviceId = deviceId, FailedVersion = failedVersion, Phase = UpdatePhase.RolledBack, UpdatedAt = factory.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
    }

    private async Task SetMinAsync(string? min)
    {
        using var scope = factory.Scope();
        await scope.ServiceProvider.GetRequiredService<ISettingsService>().SetThresholdsAsync(new HealthThresholds(60, 300, 5000, min), default);
    }

    [Fact]
    public async Task Canary_device_gets_the_target_and_non_canary_does_not()
    {
        var target = NewVersion();
        var canary = await NewDeviceAsync();
        var other = await NewDeviceAsync();
        var rollout = await NewRolloutAsync(target);
        Assert.Equal(RolloutState.Draft, rollout.State);
        Assert.Null(await TargetAsync(canary));   // Draft: nobody yet

        await SetCanaryAsync(rollout.Id, canary.Id);

        Assert.Equal(target, await TargetAsync(canary));
        Assert.Null(await TargetAsync(other));
    }

    [Fact]
    public async Task After_RolledOut_all_devices_get_the_target()
    {
        var target = NewVersion();
        var canary = await NewDeviceAsync();
        var other = await NewDeviceAsync();
        var rollout = await NewRolloutAsync(target);
        await SetCanaryAsync(rollout.Id, canary.Id);
        await SetRolloutStateAsync(rollout.Id, RolloutState.RolledOut);

        Assert.Equal(target, await TargetAsync(other));
        Assert.Equal(target, await TargetAsync(canary));
    }

    [Fact]
    public async Task A_device_already_on_the_target_stays_put()
    {
        var target = NewVersion();
        var device = await NewDeviceAsync(target);
        var rollout = await NewRolloutAsync(target);
        await SetCanaryAsync(rollout.Id, device.Id);

        Assert.Null(await TargetAsync(device));
    }

    [Fact]
    public async Task A_device_whose_FailedVersion_equals_the_target_is_not_targeted()
    {
        var target = NewVersion();
        var device = await NewDeviceAsync();
        var rollout = await NewRolloutAsync(target);
        await SetCanaryAsync(rollout.Id, device.Id);
        await SetRolloutStateAsync(rollout.Id, RolloutState.RolledOut);
        await SetFailedVersionAsync(device.Id, target);
        try
        {
            // Quarantine beats the wave AND the MinAgentVersion floor, otherwise rollback and re-target would loop forever.
            await SetMinAsync(target);
            Assert.Null(await TargetAsync(device));
        }
        finally { await SetMinAsync(null); }
    }

    [Fact]
    public async Task MinAgentVersion_floors_a_below_min_device_even_outside_the_wave()
    {
        var target = NewVersion();
        var canary = await NewDeviceAsync();
        var below = await NewDeviceAsync("1.0.0.0");
        var rollout = await NewRolloutAsync(target);
        await SetCanaryAsync(rollout.Id, canary.Id);
        try
        {
            Assert.Null(await TargetAsync(below));
            await SetMinAsync("2.0.0.0");
            Assert.Equal(target, await TargetAsync(below));
        }
        finally { await SetMinAsync(null); }
    }

    [Fact]
    public async Task MinAgentVersion_cannot_be_set_to_a_version_with_unresolved_failures()
    {
        var bad = NewVersion();
        var device = await NewDeviceAsync();
        await SetFailedVersionAsync(device.Id, bad);

        using var scope = factory.Scope();
        var admin = scope.ServiceProvider.GetRequiredService<ISettingsAdminService>();
        var ex = await Assert.ThrowsAsync<ConflictException>(() => admin.UpdateThresholdsAsync(new HealthThresholds(60, 300, 5000, bad), default));
        Assert.Contains("rolled back", ex.Message, StringComparison.OrdinalIgnoreCase);

        var saved = await admin.UpdateThresholdsAsync(new HealthThresholds(60, 300, 5000, NewVersion()), default);   // an unaffected version is fine
        Assert.NotEqual(bad, saved.MinAgentVersion);
        await SetMinAsync(null);
    }

    [Fact]
    public async Task HasAssignedInProgressUpdate_is_opened_only_by_the_server_serving_the_package_and_expires()
    {
        var device = await NewDeviceAsync("1.0.0.0");
        using (var scope = factory.Scope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IAgentUpdateService>();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            Assert.False(await svc.HasAssignedInProgressUpdateAsync(device.Id, default));
            // An agent-written in-progress state alone must NOT count.
            db.DeviceUpdateStates.Add(new DeviceUpdateState { Id = Guid.NewGuid(), DeviceId = device.Id, AssignedVersion = "9.9.9.0", Phase = UpdatePhase.Downloading, UpdatedAt = factory.Clock.GetUtcNow() });
            await db.SaveChangesAsync();
            Assert.False(await svc.HasAssignedInProgressUpdateAsync(device.Id, default));
        }

        var target = NewVersion();
        var rollout = await NewRolloutAsync(target);
        await SetCanaryAsync(rollout.Id, device.Id);
        using var scope2 = factory.Scope();
        var updates = scope2.ServiceProvider.GetRequiredService<IAgentUpdateService>();
        Assert.False(await updates.HasAssignedInProgressUpdateAsync(device.Id, default));   // assigned, but nothing served yet

        await updates.GetAssignedPackageAsync(device.Id, target, default);                  // the server serves it: window opens
        Assert.True(await updates.HasAssignedInProgressUpdateAsync(device.Id, default));

        factory.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.False(await updates.HasAssignedInProgressUpdateAsync(device.Id, default));   // window closed
    }
}
