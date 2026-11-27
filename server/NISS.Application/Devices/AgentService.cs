using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Application.Health;
using NISS.Application.Live;
using NISS.Application.Policies;
using NISS.Application.Settings;
using NISS.Application.Updates;
using NISS.Domain;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Devices;

/// <summary>Agent-facing operations other than event ingestion.</summary>
public interface IAgentService
{
    Task<HeartbeatResponse> HeartbeatAsync(Guid deviceId, HeartbeatRequest request, CancellationToken ct);
    Task UpdateStatusAsync(Guid deviceId, AgentStatusRequest request, CancellationToken ct);
    Task<AgentConfigResponse> GetConfigAsync(Guid deviceId, CancellationToken ct);
}

internal sealed class AgentService(
    IAppDbContext db, TimeProvider clock, ISettingsService settings, IPolicyService policies, IDeviceStatusService statusService,
    ISecurityEventService security, INotificationBuffer buffer, ILiveStateService live, IAgentUpdateService updates) : IAgentService
{
    private static readonly TimeSpan MaxLiveSkew = TimeSpan.FromMinutes(5);

    public async Task<HeartbeatResponse> HeartbeatAsync(Guid deviceId, HeartbeatRequest req, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw new NotFoundException("Device not found.");
        if (!string.Equals(req.DeviceId, device.DeviceCode, StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException("deviceId does not match the authenticated device.", nameof(req.DeviceId));

        var thresholds = await settings.GetThresholdsAsync(ct);
        var policyVersion = await settings.GetPolicyVersionAsync(ct);

        // Disabled / revoked: tell the agent, record nothing.
        if (device.Status is DeviceHealth.Disabled or DeviceHealth.Revoked)
            return new HeartbeatResponse(now, device.Status, policyVersion, thresholds.MinAgentVersion, null);

        db.Heartbeats.Add(new Heartbeat
        {
            Id = Guid.NewGuid(), DeviceId = device.Id, Timestamp = now, DeviceTimestamp = req.TimestampUtc.ToUniversalTime(),
            AgentVersion = Text.Trunc(req.AgentVersion, 64) ?? "", UptimeSeconds = req.UptimeSeconds, ServiceState = Text.Trunc(req.ServiceState, 32) ?? "",
            QueueDepth = req.QueueDepth, ClockOffsetMs = req.ClockOffsetMs, CpuPercent = req.Live?.CpuPercent, MemoryUsedPercent = req.Live?.MemoryUsedPercent,
        });

        device.LastSeen = now;
        device.ServiceStoppedAt = null;       // a heartbeat supersedes any earlier AGENT_STOPPED
        device.AgentVersion = Text.Trunc(req.AgentVersion, 64) ?? device.AgentVersion;
        device.UpdatedAt = now;
        if (req.UptimeSeconds > 0) device.LastBootAt = now.AddSeconds(-req.UptimeSeconds);
        await statusService.ApplyAsync(device, DeviceHealth.Online, "heartbeat", ct);

        var status = await GetOrCreateStatusAsync(device.Id, now, ct);
        var deviceLiveTime = req.TimestampUtc.AddMilliseconds(req.ClockOffsetMs);
        var liveAt = deviceLiveTime > now || now - deviceLiveTime > MaxLiveSkew ? now : deviceLiveTime;
        status.ServiceState = Text.Trunc(req.ServiceState, 32) ?? "";
        status.QueueDepth = req.QueueDepth;
        status.OldestQueuedAt = req.OldestQueuedUtc;
        status.LastUploadAt = req.LastUploadUtc ?? status.LastUploadAt;
        status.LastError = Text.Trunc(req.LastError, 1000);
        status.ClockOffsetMs = req.ClockOffsetMs;
        status.ConfigVersion = req.ConfigVersion;
        status.SessionAgentsJson = JsonColumn.Serialize(req.SessionAgents);
        status.LastHeartbeatAt = now;
        status.UpdatedAt = now;
        ApplyLive(status, req.Live, liveAt);

        await EvaluateIssuesAsync(device, status, req.SessionAgents, req.Live is { Presence: Presence.Active or Presence.Idle or Presence.Locked }, thresholds, ct);

        await db.SaveChangesAsync(ct);
        await buffer.FlushAsync(ct);
        await live.PublishAsync(device.Id, ct);

        var effective = await policies.GetEffectiveAsync(device.DeviceCode, await DepartmentAsync(device, ct), ct);
        // Hot path: two indexed reads (newest live rollout + this device's update-state row).
        var target = await updates.TargetVersionForAsync(device, device.AgentVersion, ct);
        return new HeartbeatResponse(now, "ACTIVE", policyVersion, thresholds.MinAgentVersion ?? effective.MinAgentVersion, effective.LatestAgentVersion,
            target, target is null ? null : $"/api/v1/agent/update/{target}");
    }

    private static void ApplyLive(AgentStatus s, LiveState? l, DateTimeOffset liveAt)
    {
        if (l is null) return;
        s.LiveUpdatedAt = liveAt;
        s.Presence = string.IsNullOrWhiteSpace(l.Presence) ? Presence.Unknown : l.Presence;
        s.UserName = Text.Trunc(l.UserName, 256);
        s.WindowsSessionId = l.WindowsSessionId;
        s.CurrentProcessId = l.Foreground?.ProcessId;
        s.CurrentApplication = Text.Trunc(l.Foreground?.ProcessName, 260);
        s.CurrentExecutablePath = Text.Trunc(l.Foreground?.ExecutablePath, 1024);
        s.CurrentWindow = Text.Trunc(l.Foreground?.WindowTitle, 512);
        s.OpenApplicationsJson = JsonColumn.Serialize(l.OpenApplications);
        s.IdleSeconds = l.IdleSeconds;
        s.Interacting = l.Interacting;
        s.LastInputAt = l.LastInputUtc;
        s.CpuPercent = l.CpuPercent;
        s.MemoryUsedPercent = l.MemoryUsedPercent;
        s.NetworkConnected = l.NetworkConnected;
    }

    private async Task EvaluateIssuesAsync(Device device, AgentStatus status, IReadOnlyList<SessionAgentInfo>? sessionAgents, bool userLoggedIn,
        HealthThresholds thresholds, CancellationToken ct)
    {
        var dref = new DeviceRef(device.Id, device.DeviceCode);

        if (status.QueueDepth >= thresholds.BacklogWarningThreshold && thresholds.BacklogWarningThreshold > 0)
            await security.RaiseAsync(dref, SecurityEventKinds.AgentBacklogWarning, SecuritySeverity.Warning,
                $"{device.DeviceCode}: {status.QueueDepth} events are waiting to be uploaded.", ct: ct);
        else
            await security.ResolveAsync(device.Id, ct, SecurityEventKinds.AgentBacklogWarning);

        var agentMissing = userLoggedIn && (sessionAgents is null || !sessionAgents.Any(a => a.Connected));
        if (agentMissing)
            await security.RaiseAsync(dref, SecurityEventKinds.SessionAgentMissing, SecuritySeverity.Warning,
                $"{device.DeviceCode}: a user is logged in but the session agent is not running.", ct: ct);
        else
            await security.ResolveAsync(device.Id, ct, SecurityEventKinds.SessionAgentMissing);

        if (HealthEvaluator.IsOutdated(device.AgentVersion, thresholds.MinAgentVersion))
            await security.RaiseAsync(dref, SecurityEventKinds.AgentOutdated, SecuritySeverity.Warning,
                $"{device.DeviceCode}: agent {device.AgentVersion} is older than the required {thresholds.MinAgentVersion}.", ct: ct);
        else
            await security.ResolveAsync(device.Id, ct, SecurityEventKinds.AgentOutdated);

        if (!string.IsNullOrWhiteSpace(status.LastUploadError))
            await security.RaiseAsync(dref, SecurityEventKinds.UploadFailure, SecuritySeverity.Warning,
                $"{device.DeviceCode}: uploads are failing: {Text.Trunc(status.LastUploadError, 200)}", ct: ct);
        else
            await security.ResolveAsync(device.Id, ct, SecurityEventKinds.UploadFailure);
    }

    public async Task UpdateStatusAsync(Guid deviceId, AgentStatusRequest req, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw new NotFoundException("Device not found.");
        if (!string.Equals(req.DeviceId, device.DeviceCode, StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException("deviceId does not match the authenticated device.", nameof(req.DeviceId));
        if (device.Status is DeviceHealth.Disabled or DeviceHealth.Revoked) throw new ForbiddenException($"Device is {device.Status}.");

        var thresholds = await settings.GetThresholdsAsync(ct);
        var status = await GetOrCreateStatusAsync(device.Id, now, ct);
        status.CollectorsJson = JsonColumn.Serialize(req.Collectors);
        status.QueueDepth = req.QueueDepth;
        status.DeadLetterCount = req.DeadLetterCount;
        status.OldestQueuedAt = req.OldestQueuedUtc;
        status.LastUploadAt = req.LastUploadUtc ?? status.LastUploadAt;
        status.LastUploadError = Text.Trunc(req.LastUploadError, 1000);
        status.LastError = Text.Trunc(req.LastError, 1000);
        status.ServerReachable = req.ServerReachable;
        status.SessionAgentsJson = JsonColumn.Serialize(req.SessionAgents);
        status.CpuPercent = req.CpuPercent ?? status.CpuPercent;
        status.MemoryUsedPercent = req.MemoryUsedPercent ?? status.MemoryUsedPercent;
        status.DiskUsedPercent = req.DiskUsedPercent ?? status.DiskUsedPercent;
        status.UpdatedAt = now;

        device.AgentVersion = Text.Trunc(req.AgentVersion, 64) ?? device.AgentVersion;
        device.Hostname = Text.Trunc(req.Hostname, 255) ?? device.Hostname;
        device.OsVersion = Text.Trunc(req.OsVersion, 256) ?? device.OsVersion;
        device.Os = device.Os ?? device.OsVersion;
        device.Manufacturer = Text.Trunc(req.Manufacturer, 128) ?? device.Manufacturer;
        device.Model = Text.Trunc(req.Model, 128) ?? device.Model;
        device.MachineSerial = Text.Trunc(req.SerialNumber, 128) ?? device.MachineSerial;
        device.UpdatedAt = now;

        var failing = req.Collectors.Where(c => string.Equals(c.State, "Failed", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(c.LastError)).ToList();
        if (failing.Count > 0)
            await security.RaiseAsync(new DeviceRef(device.Id, device.DeviceCode), SecurityEventKinds.CollectorFailure, SecuritySeverity.Warning,
                $"{device.DeviceCode}: collector(s) failing: {string.Join(", ", failing.Select(c => c.Name))}", onlyIfNoneOpen: false, throttle: TimeSpan.FromMinutes(30), ct: ct);

        await EvaluateIssuesAsync(device, status, req.SessionAgents, status.Presence is Presence.Active or Presence.Idle or Presence.Locked, thresholds, ct);
        await db.SaveChangesAsync(ct);
        await buffer.FlushAsync(ct);
    }

    public async Task<AgentConfigResponse> GetConfigAsync(Guid deviceId, CancellationToken ct)
    {
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, ct) ?? throw new NotFoundException("Device not found.");
        if (device.Status is DeviceHealth.Disabled or DeviceHealth.Revoked) throw new ForbiddenException($"Device is {device.Status}.");
        var dept = await DepartmentAsync(device, ct);
        var employeeCode = device.EmployeeId is { } id ? await db.Employees.Where(e => e.Id == id).Select(e => e.EmployeeCode).FirstOrDefaultAsync(ct) : null;
        return new AgentConfigResponse(device.DeviceCode, employeeCode, clock.GetUtcNow(), await policies.GetEffectiveAsync(device.DeviceCode, dept, ct));
    }

    private async Task<string?> DepartmentAsync(Device d, CancellationToken ct) =>
        d.EmployeeId is { } id ? await db.Employees.AsNoTracking().Where(e => e.Id == id).Select(e => e.Department).FirstOrDefaultAsync(ct) : null;

    private async Task<AgentStatus> GetOrCreateStatusAsync(Guid deviceId, DateTimeOffset now, CancellationToken ct)
    {
        var row = await db.AgentStatuses.FindAsync([deviceId], ct);
        if (row is not null) return row;
        row = new AgentStatus { DeviceId = deviceId, LiveUpdatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = now };
        db.AgentStatuses.Add(row);
        return row;
    }
}
