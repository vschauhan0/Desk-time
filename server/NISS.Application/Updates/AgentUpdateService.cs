using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NISS.Application.Abstractions;
using NISS.Application.Audit;
using NISS.Application.Common;
using NISS.Application.Health;
using NISS.Application.Options;
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

    /// <summary>True while the server's live rollout assigns this device an update it has not yet reached (server state only).</summary>
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

    /// <summary>Clears the device's <see cref="DeviceUpdateState.FailedVersion"/> once it runs a version at or above it (it has moved past the failure).</summary>
    Task ClearRecoveredFailureAsync(Guid deviceId, string currentVersion, CancellationToken ct);

    Task<AgentRolloutDto> GetRolloutAsync(Guid rolloutId, CancellationToken ct);

    /// <summary>Canary outcome counts and whether the gate to "roll out to all" is open.</summary>
    Task<RolloutHealth> CanaryHealthAsync(Guid rolloutId, CancellationToken ct);

    /// <summary>Rolls out to everyone. Refused (409) until the canary is healthy AND baked.</summary>
    Task PromoteAsync(Guid rolloutId, CancellationToken ct);

    Task PauseAsync(Guid rolloutId, CancellationToken ct);

    Task RollbackAsync(Guid rolloutId, CancellationToken ct);
}

/// <summary>
/// Canary outcome. <see cref="Attempted"/> = devices that reported an outcome (updated, or failed) while alive; offline / never-seen /
/// still-in-flight devices are <see cref="Pending"/> and excluded from the gate. <see cref="Updated"/> = reported updated (healthy or not).
/// </summary>
public sealed record RolloutHealth(int Canary, int Attempted, int Updated, int Healthy, int Failed, int Pending, DateTimeOffset? BakedAt, bool GateOpen, string? Reason);

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
    ISecurityEventService security, INotificationBuffer buffer, IOptions<AgentUpdateOptions> options) : IAgentUpdateService
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

    public async Task ClearRecoveredFailureAsync(Guid deviceId, string currentVersion, CancellationToken ct)
    {
        var state = await db.DeviceUpdateStates.FirstOrDefaultAsync(s => s.DeviceId == deviceId && s.FailedVersion != null, ct);
        if (state?.FailedVersion is not { } failed || !VersionText.AtOrAbove(currentVersion, failed)) return;
        state.FailedVersion = null;
        state.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task<AgentRolloutDto> GetRolloutAsync(Guid rolloutId, CancellationToken ct)
    {
        var rollout = await FindAsync(rolloutId, ct);
        var h = await EvaluateAsync(rollout, ct);
        return new AgentRolloutDto(rollout.Id.ToString(), rollout.Version, rollout.State, rollout.CreatedAt, h.Canary, h.Updated, h.Failed, h.Pending);
    }

    public async Task<RolloutHealth> CanaryHealthAsync(Guid rolloutId, CancellationToken ct) => await EvaluateAsync(await FindAsync(rolloutId, ct), ct);

    public async Task PromoteAsync(Guid rolloutId, CancellationToken ct)
    {
        var rollout = await FindAsync(rolloutId, ct);
        if (rollout.State is not (RolloutState.Canary or RolloutState.Paused))
            throw new ConflictException($"A {rollout.State} rollout cannot be rolled out; it needs a canary first.");
        var health = await EvaluateAsync(rollout, ct);
        if (!health.GateOpen) throw new ConflictException($"Canary is not ready: {health.Reason}");
        await TransitionAsync(rollout, RolloutState.RolledOut, AuditActions.RolloutPromoted, ct);
    }

    public async Task PauseAsync(Guid rolloutId, CancellationToken ct)
    {
        var rollout = await FindAsync(rolloutId, ct);
        if (rollout.State is not (RolloutState.Canary or RolloutState.RolledOut))
            throw new ConflictException($"A {rollout.State} rollout cannot be paused.");
        await TransitionAsync(rollout, RolloutState.Paused, AuditActions.RolloutPaused, ct);
    }

    public async Task RollbackAsync(Guid rolloutId, CancellationToken ct)
    {
        var rollout = await FindAsync(rolloutId, ct);
        if (rollout.State is not (RolloutState.Canary or RolloutState.RolledOut or RolloutState.Paused))
            throw new ConflictException($"A {rollout.State} rollout cannot be rolled back.");
        // Devices that already failed keep their FailedVersion quarantine; a RolledBack rollout is never targeted, so nobody else is assigned it.
        await TransitionAsync(rollout, RolloutState.RolledBack, AuditActions.RolloutRolledBack, ct);
    }

    private async Task<AgentRollout> FindAsync(Guid id, CancellationToken ct) =>
        await db.AgentRollouts.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Rollout not found.");

    private async Task TransitionAsync(AgentRollout rollout, string state, string auditAction, CancellationToken ct)
    {
        rollout.State = state;
        rollout.UpdatedAt = clock.GetUtcNow();
        audit.Record(auditAction, rollout.Version, $"rollout={rollout.Id}");
        await db.SaveChangesAsync(ct);
    }

    private async Task<RolloutHealth> EvaluateAsync(AgentRollout rollout, CancellationToken ct)
    {
        var ids = rollout.CanaryDeviceIds;
        var now = clock.GetUtcNow();
        var fresh = TimeSpan.FromMinutes(Math.Max(options.Value.CanaryStatusFreshMinutes, 1));
        var states = await db.DeviceUpdateStates.AsNoTracking().Where(s => ids.Contains(s.DeviceId)).ToListAsync(ct);
        var statuses = await db.AgentStatuses.AsNoTracking().Where(s => ids.Contains(s.DeviceId)).ToDictionaryAsync(s => s.DeviceId, ct);

        int updated = 0, healthy = 0, failed = 0;
        DateTimeOffset? lastCompletion = null;
        foreach (var state in states)
        {
            if (!VersionText.Same(state.AssignedVersion, rollout.Version)) continue;
            if (VersionText.Same(state.FailedVersion, rollout.Version)) { failed++; continue; }   // unresolved failure (cleared on recovery)
            if (state.Phase != UpdatePhase.Succeeded) continue;                                    // in flight: pending

            // Offline / never-seen devices are pending, not failures: only a live heartbeat puts a device in the denominator.
            if (!statuses.TryGetValue(state.DeviceId, out var status) || now - (status.LastHeartbeatAt ?? status.UpdatedAt) > fresh) continue;
            updated++;
            lastCompletion = lastCompletion is { } l && l > state.UpdatedAt ? l : state.UpdatedAt;
            if (CollectorsHealthy(status)) healthy++;
        }

        var attempted = updated + failed;
        var pending = ids.Length - attempted;
        var bakedAt = lastCompletion?.AddHours(Math.Max(rollout.BakeHours, 0));
        var ratio = attempted == 0 ? 0 : (double)healthy / attempted;

        string? reason = null;
        if (attempted == 0) reason = "no canary device has completed the update yet.";
        else if (failed > 0) reason = $"{failed} canary device(s) failed or rolled back.";
        else if (ratio < options.Value.CanaryMinHealthyRatio) reason = $"only {healthy} of {attempted} updated canary device(s) have healthy collectors.";
        else if (bakedAt is { } b && now < b) reason = $"the bake window runs until {b:u}.";
        return new RolloutHealth(ids.Length, attempted, updated, healthy, failed, pending, bakedAt, reason is null, reason);
    }

    /// <summary>All collectors Running; when the device reports user sessions at least one must be attached (no sessions reported = headless, fine).</summary>
    private static bool CollectorsHealthy(AgentStatus status)
    {
        var collectors = JsonColumn.DeserializeList<CollectorStatus>(status.CollectorsJson);
        if (collectors.Count == 0 || collectors.Any(c => !string.Equals(c.State, "Running", StringComparison.OrdinalIgnoreCase))) return false;
        var sessions = JsonColumn.DeserializeList<SessionAgentInfo>(status.SessionAgentsJson);
        return sessions.Count == 0 || sessions.Any(s => s.Connected);
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

    /// <summary>
    /// SERVER-AUTHORITATIVE and bounded by a server-recorded event: true only within <c>UpdateRestartWindowMinutes</c> of the server serving this
    /// device its assigned package (<see cref="DeviceUpdateState.PackageServedAtUtc"/>, written only by <see cref="GetAssignedPackageAsync"/> on the first download of a version).
    /// Nothing the agent reports (its version, status phase) can open or extend the window; the FailedVersion quarantine vetoes it.
    /// </summary>
    public async Task<bool> HasAssignedInProgressUpdateAsync(Guid deviceId, CancellationToken ct)
    {
        var state = await db.DeviceUpdateStates.AsNoTracking().FirstOrDefaultAsync(s => s.DeviceId == deviceId, ct);
        if (state?.PackageServedAtUtc is not { } served) return false;
        if (VersionText.Same(state.FailedVersion, state.AssignedVersion)) return false;
        var window = TimeSpan.FromMinutes(Math.Max(options.Value.UpdateRestartWindowMinutes, 0));
        return clock.GetUtcNow() - served <= window;
    }

    public async Task<AgentPackage> GetAssignedPackageAsync(Guid deviceId, string version, CancellationToken ct)
    {
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw new NotFoundException("Device not found.");
        if (device.Status is DeviceHealth.Disabled or DeviceHealth.Revoked) throw new ForbiddenException($"Device is {device.Status}.");
        var target = await TargetVersionForAsync(device, device.AgentVersion, ct);
        if (target is null || !VersionText.Same(target, version)) throw new ForbiddenException("That version is not assigned to this device.");
        var package = await db.AgentPackages.AsNoTracking().FirstOrDefaultAsync(p => p.Version == target, ct)
            ?? throw new NotFoundException($"No uploaded package for version {target}.");

        // Server-recorded: the FIRST download of a version opens the one update-restart window for it. A re-download of the same version
        // must not refresh it (the agent holds the secret and could otherwise keep suppression open forever).
        var state = await db.DeviceUpdateStates.FirstOrDefaultAsync(s => s.DeviceId == deviceId, ct);
        if (state is null) db.DeviceUpdateStates.Add(state = new DeviceUpdateState { Id = Guid.NewGuid(), DeviceId = deviceId, Phase = UpdatePhase.Assigned });
        state.AssignedVersion = target;
        if (!VersionText.Same(state.PackageServedVersion, target))
        {
            state.PackageServedVersion = target;
            state.PackageServedAtUtc = clock.GetUtcNow();
        }
        state.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return package;
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
