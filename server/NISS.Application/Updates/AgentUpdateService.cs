using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Application.Audit;
using NISS.Application.Common;
using NISS.Application.Health;
using NISS.Application.Settings;
using NISS.Domain;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Updates;

public interface IAgentUpdateService
{
    /// <summary>Creates a Draft rollout of an already-uploaded package version.</summary>
    Task<AgentRollout> CreateRolloutAsync(string version, string? notes, string actor, CancellationToken ct);

    /// <summary>Chooses the canary devices and starts the Canary wave.</summary>
    Task SetCanaryAsync(Guid rolloutId, IReadOnlyList<Guid> deviceIds, string actor, CancellationToken ct);

    /// <summary>
    /// The version this device should move to, or null to stay put. Wave membership (canary set, or everyone once RolledOut) and the
    /// MinAgentVersion floor decide; the device's own FailedVersion quarantine vetoes both, so a rolled-back device is never re-targeted.
    /// </summary>
    Task<string?> TargetVersionForAsync(Device device, string currentVersion, CancellationToken ct);

    /// <summary>True while the device has an assigned update that has not yet finished or failed.</summary>
    Task<bool> HasAssignedInProgressUpdateAsync(Guid deviceId, CancellationToken ct);

    /// <summary>
    /// The stored package for <paramref name="version"/>, but only when that is exactly the version currently assigned to the device
    /// (a device may fetch only its own target); otherwise <see cref="ForbiddenException"/>.
    /// </summary>
    Task<AgentPackage> GetAssignedPackageAsync(Guid deviceId, string version, CancellationToken ct);

    /// <summary>
    /// Upserts the device's update progress. A rolled_back/failed report records <see cref="DeviceUpdateState.FailedVersion"/> (the
    /// quarantine <see cref="TargetVersionForAsync"/> reads) and raises an UPDATE_FAILED security event.
    /// </summary>
    Task ReportStatusAsync(Guid deviceId, UpdateStatusReport report, CancellationToken ct);
}

internal static class VersionText
{
    /// <summary>Equal as dotted versions (1.4 == 1.4.0.0); falls back to ordinal comparison when either is unparsable.</summary>
    public static bool Same(string? a, string? b)
    {
        if (a is null || b is null) return false;
        return Version.TryParse(a.Trim(), out var va) && Version.TryParse(b.Trim(), out var vb)
            ? Normalize(va) == Normalize(vb)
            : string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="current"/> is at or above <paramref name="target"/> (unparsable: only an exact match counts).</summary>
    public static bool AtOrAbove(string? current, string target) =>
        Version.TryParse(current?.Trim(), out var c) && Version.TryParse(target.Trim(), out var t) ? Normalize(c) >= Normalize(t) : Same(current, target);

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}

internal sealed class AgentUpdateService(IAppDbContext db, ISettingsService settings, IAuditService audit, TimeProvider clock,
    ISecurityEventService security, INotificationBuffer buffer) : IAgentUpdateService
{
    public async Task<AgentRollout> CreateRolloutAsync(string version, string? notes, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(version)) throw new BadRequestException("Version is required.", nameof(version));
        version = version.Trim();
        if (!await db.AgentPackages.AnyAsync(p => p.Version == version, ct))
            throw new NotFoundException($"No uploaded package for version {version}.");

        var now = clock.GetUtcNow();
        var rollout = new AgentRollout
        {
            Id = Guid.NewGuid(), Version = version, State = RolloutState.Draft, Notes = Text.Trunc(notes, 2000),
            CreatedBy = Text.Trunc(actor, 256), CreatedAt = now, UpdatedAt = now,
        };
        db.AgentRollouts.Add(rollout);
        audit.Record(AuditActions.RolloutCreated, version, $"rollout={rollout.Id}");
        await db.SaveChangesAsync(ct);
        return rollout;
    }

    public async Task SetCanaryAsync(Guid rolloutId, IReadOnlyList<Guid> deviceIds, string actor, CancellationToken ct)
    {
        var rollout = await db.AgentRollouts.FirstOrDefaultAsync(r => r.Id == rolloutId, ct) ?? throw new NotFoundException("Rollout not found.");
        if (rollout.State is not (RolloutState.Draft or RolloutState.Canary))
            throw new ConflictException($"A {rollout.State} rollout can no longer change its canary set.");
        var ids = deviceIds.Distinct().ToArray();
        if (ids.Length == 0) throw new BadRequestException("Choose at least one canary device.", nameof(deviceIds));
        if (await db.Devices.CountAsync(d => ids.Contains(d.Id), ct) != ids.Length)
            throw new BadRequestException("One or more canary devices do not exist.", nameof(deviceIds));

        var now = clock.GetUtcNow();
        rollout.CanaryDeviceIds = ids;
        rollout.State = RolloutState.Canary;
        rollout.CanaryStartedAt ??= now;
        rollout.UpdatedAt = now;
        audit.Record(AuditActions.RolloutCanarySet, rollout.Version, $"rollout={rollout.Id}; devices={ids.Length}; by={Text.Trunc(actor, 256)}");
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> TargetVersionForAsync(Device device, string currentVersion, CancellationToken ct)
    {
        // Only a live wave (Canary / RolledOut) targets anyone; the newest one wins.
        var rollout = await db.AgentRollouts.AsNoTracking()
            .Where(r => r.State == RolloutState.Canary || r.State == RolloutState.RolledOut)
            .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        if (rollout is null) return null;
        var target = rollout.Version;

        // Quarantine first: it vetoes the wave and the MinAgentVersion floor alike (no rollback/re-target loop).
        var failed = await db.DeviceUpdateStates.AsNoTracking().Where(s => s.DeviceId == device.Id).Select(s => s.FailedVersion).FirstOrDefaultAsync(ct);
        if (VersionText.Same(failed, target)) return null;

        if (VersionText.AtOrAbove(currentVersion, target)) return null;

        var inWave = rollout.State == RolloutState.RolledOut
            || (rollout.State == RolloutState.Canary && rollout.CanaryDeviceIds.Contains(device.Id));
        if (inWave) return target;

        var thresholds = await settings.GetThresholdsAsync(ct);
        return HealthEvaluator.IsOutdated(currentVersion, thresholds.MinAgentVersion) ? target : null;
    }

    public Task<bool> HasAssignedInProgressUpdateAsync(Guid deviceId, CancellationToken ct) =>
        db.DeviceUpdateStates.AsNoTracking().AnyAsync(s => s.DeviceId == deviceId && s.AssignedVersion != null && UpdatePhase.InProgress.Contains(s.Phase), ct);

    public async Task<AgentPackage> GetAssignedPackageAsync(Guid deviceId, string version, CancellationToken ct)
    {
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw new NotFoundException("Device not found.");
        if (device.Status is DeviceHealth.Disabled or DeviceHealth.Revoked) throw new ForbiddenException($"Device is {device.Status}.");
        var target = await TargetVersionForAsync(device, device.AgentVersion, ct);
        if (target is null || !VersionText.Same(target, version)) throw new ForbiddenException("That version is not assigned to this device.");
        return await db.AgentPackages.AsNoTracking().FirstOrDefaultAsync(p => p.Version == target, ct)
            ?? throw new NotFoundException($"No uploaded package for version {target}.");
    }

    public async Task ReportStatusAsync(Guid deviceId, UpdateStatusReport report, CancellationToken ct)
    {
        var phase = MapPhase(report.Phase) ?? throw new BadRequestException("Unknown update phase.", nameof(report.Phase));
        if (string.IsNullOrWhiteSpace(report.Version)) throw new BadRequestException("Version is required.", nameof(report.Version));
        var version = Text.Trunc(report.Version.Trim(), 64)!;
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw new NotFoundException("Device not found.");
        var now = clock.GetUtcNow();

        var state = await db.DeviceUpdateStates.FirstOrDefaultAsync(s => s.DeviceId == deviceId, ct);
        if (state is null)
        {
            state = new DeviceUpdateState { Id = Guid.NewGuid(), DeviceId = deviceId };
            db.DeviceUpdateStates.Add(state);
        }
        state.AssignedVersion = version;
        state.Phase = phase;
        state.Attempts = Math.Max(report.Attempt, 0);
        state.LastError = Text.Trunc(report.Error, 1000);
        state.UpdatedAt = now;

        if (phase is UpdatePhase.RolledBack or UpdatePhase.Failed)
        {
            state.FailedVersion = version;   // quarantine: TargetVersionForAsync never re-targets this version for this device
            await security.RaiseAsync(new DeviceRef(device.Id, device.DeviceCode), SecurityEventKinds.UpdateFailed, SecuritySeverity.Warning,
                $"{device.DeviceCode}: update to {version} {(phase == UpdatePhase.RolledBack ? "was rolled back" : "failed")}"
                + (string.IsNullOrWhiteSpace(state.LastError) ? "." : $": {Text.Trunc(state.LastError, 200)}"),
                onlyIfNoneOpen: false, ct: ct);
        }
        else if (phase == UpdatePhase.Succeeded && VersionText.Same(state.FailedVersion, version))
            state.FailedVersion = null;

        await db.SaveChangesAsync(ct);
        await buffer.FlushAsync(ct);
    }

    private static string? MapPhase(string? phase) => phase?.Trim().ToLowerInvariant() switch
    {
        "downloading" => UpdatePhase.Downloading,
        "verifying" or "installing" or "health_check" => UpdatePhase.Applying,
        "updated" => UpdatePhase.Succeeded,
        "failed" => UpdatePhase.Failed,
        "rolled_back" => UpdatePhase.RolledBack,
        _ => null,
    };
}
