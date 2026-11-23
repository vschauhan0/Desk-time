using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NISS.Application.Abstractions;
using NISS.Application.Live;
using NISS.Application.Settings;
using NISS.Domain;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Health;

/// <summary>Pure implementation of the health table in docs/api.md.</summary>
public static class HealthEvaluator
{
    /// <summary>True for states set only by an administrator (never computed).</summary>
    public static bool IsAdminState(string status) => status is DeviceHealth.Disabled or DeviceHealth.Revoked;

    /// <summary>Computes the health state from the device's timestamps.</summary>
    public static string Evaluate(string currentStatus, DateTimeOffset? lastSeen, DateTimeOffset? serviceStoppedAt, DateTimeOffset now, HealthThresholds t)
    {
        if (IsAdminState(currentStatus)) return currentStatus;
        if (lastSeen is null && serviceStoppedAt is null) return DeviceHealth.Pending;
        if (serviceStoppedAt is { } stopped && (lastSeen is null || lastSeen <= stopped)) return DeviceHealth.ServiceStopped;
        if (lastSeen is null) return DeviceHealth.Pending;
        var age = (now - lastSeen.Value).TotalSeconds;
        if (age <= t.HeartbeatWarningSeconds) return DeviceHealth.Online;
        if (age <= t.OfflineSeconds) return DeviceHealth.Warning;
        return DeviceHealth.Offline;
    }

    /// <summary>True when <paramref name="agentVersion"/> is lower than <paramref name="minVersion"/> (unparsable values are never "outdated").</summary>
    public static bool IsOutdated(string? agentVersion, string? minVersion)
    {
        if (!TryParse(agentVersion, out var current) || !TryParse(minVersion, out var min)) return false;
        return current < min;
    }

    private static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value)) return false;
        var core = value.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(core, out version!);
    }
}

/// <summary>Applies a device state transition: updates the row and stages security events + notifications.</summary>
public interface IDeviceStatusService
{
    /// <summary>Returns true when the status changed. The caller saves and flushes the notification buffer.</summary>
    Task<bool> ApplyAsync(Device device, string newStatus, string? reason, CancellationToken ct);
}

internal sealed class DeviceStatusService(TimeProvider clock, ISecurityEventService events, INotificationBuffer buffer) : IDeviceStatusService
{
    public async Task<bool> ApplyAsync(Device device, string newStatus, string? reason, CancellationToken ct)
    {
        var old = device.Status;
        if (old == newStatus) return false;
        var now = clock.GetUtcNow();
        device.Status = newStatus;
        device.UpdatedAt = now;
        var dref = new DeviceRef(device.Id, device.DeviceCode);
        buffer.Add(new DeviceStatusChange(device.DeviceCode, newStatus, old, reason, now));

        switch (newStatus)
        {
            case DeviceHealth.Warning:
                await events.RaiseAsync(dref, SecurityEventKinds.HeartbeatDelayed, SecuritySeverity.Warning,
                    $"{device.DeviceCode} ({device.Hostname}): heartbeat delayed.", ct: ct);
                break;
            case DeviceHealth.Offline:
                await events.ResolveAsync(device.Id, ct, SecurityEventKinds.HeartbeatDelayed);
                await events.RaiseAsync(dref, SecurityEventKinds.DeviceOffline, SecuritySeverity.Critical,
                    $"{device.DeviceCode} ({device.Hostname}): no heartbeat, device is offline.", ct: ct);
                break;
            case DeviceHealth.ServiceStopped:
                await events.RaiseAsync(dref, SecurityEventKinds.ServiceStopped, SecuritySeverity.Warning,
                    $"{device.DeviceCode} ({device.Hostname}): monitoring service reported a stop.", ct: ct);
                break;
            case DeviceHealth.Online when old is DeviceHealth.Warning or DeviceHealth.Offline or DeviceHealth.ServiceStopped:
                await events.ResolveAsync(device.Id, ct, SecurityEventKinds.HeartbeatDelayed, SecurityEventKinds.DeviceOffline, SecurityEventKinds.ServiceStopped);
                if (old is DeviceHealth.Offline or DeviceHealth.ServiceStopped)
                    await events.RaiseAsync(dref, SecurityEventKinds.DeviceRecovered, SecuritySeverity.Info,
                        $"{device.DeviceCode} ({device.Hostname}) is back online.", onlyIfNoneOpen: false, alreadyResolved: true, ct: ct);
                break;
        }
        return true;
    }
}

/// <summary>One health pass over all devices (called by the background worker every 15 seconds).</summary>
public interface IDeviceHealthService
{
    /// <summary>Evaluates every device, applies transitions and publishes the changes. Returns the number of changed devices.</summary>
    Task<int> RunOnceAsync(CancellationToken ct);
}

internal sealed class DeviceHealthService(
    IAppDbContext db, TimeProvider clock, ISettingsService settings, IDeviceStatusService status,
    INotificationBuffer buffer, ILiveStateService live, ILogger<DeviceHealthService> log) : IDeviceHealthService
{
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var thresholds = await settings.GetThresholdsAsync(ct);
        var devices = await db.Devices
            .Where(d => d.Status != DeviceHealth.Disabled && d.Status != DeviceHealth.Revoked)
            .ToListAsync(ct);

        var changed = new List<Guid>();
        foreach (var d in devices)
        {
            var next = HealthEvaluator.Evaluate(d.Status, d.LastSeen, d.ServiceStoppedAt, now, thresholds);
            if (await status.ApplyAsync(d, next, "health check", ct)) changed.Add(d.Id);
        }

        if (changed.Count == 0) return 0;
        await db.SaveChangesAsync(ct);
        await buffer.FlushAsync(ct);
        foreach (var id in changed) await live.PublishAsync(id, ct);
        log.LogInformation("Device health pass changed {Count} device(s)", changed.Count);
        return changed.Count;
    }
}
