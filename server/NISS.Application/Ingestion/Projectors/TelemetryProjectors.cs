using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Application.Health;
using NISS.Domain;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;
using ET = NISS.Monitor.Protocol.EventTypes;

namespace NISS.Application.Ingestion.Projectors;

/// <summary>MOUSE_ACTIVITY / KEYBOARD_ACTIVITY: merge-upsert of one bucket. Counters only.</summary>
internal sealed class InputActivityProjector(IIngestionStore store) : IEventProjector
{
    public IReadOnlyCollection<string> EventTypes { get; } = [ET.MouseActivity, ET.KeyboardActivity];

    public Task ProjectAsync(ProjectionContext ctx, CancellationToken ct)
    {
        var p = ctx.Payload<InputActivityPayload>();
        var row = new InputActivity
        {
            Id = Guid.NewGuid(), DeviceId = ctx.DeviceId, EmployeeId = ctx.EmployeeId,
            Timestamp = p.BucketStartUtc.ToUniversalTime(), BucketSeconds = Math.Max(1, p.BucketSeconds),
            KeyboardActivity = Math.Max(0, p.KeyboardEventCount ?? 0), MouseActivity = Math.Max(0, p.MouseMoveEventCount ?? 0),
            ClickCount = Math.Max(0, p.ClickCount ?? 0), DoubleClickCount = Math.Max(0, p.DoubleClickCount ?? 0), WheelCount = Math.Max(0, p.WheelCount ?? 0),
            LastKeyboardAt = p.LastKeyboardUtc?.ToUniversalTime(), LastMouseAt = p.LastMouseUtc?.ToUniversalTime(),
            OptionalCursorX = p.CursorX, OptionalCursorY = p.CursorY,
        };
        return store.UpsertInputActivityAsync(row, ct);
    }
}

/// <summary>USB_CONNECTED / USB_DISCONNECTED.</summary>
internal sealed class UsbProjector(IAppDbContext db) : IEventProjector
{
    public IReadOnlyCollection<string> EventTypes { get; } = [ET.UsbConnected, ET.UsbDisconnected];

    public Task ProjectAsync(ProjectionContext ctx, CancellationToken ct)
    {
        var p = ctx.Payload<UsbPayload>();
        db.UsbEvents.Add(new UsbEvent
        {
            Id = ctx.Envelope.EventId, DeviceId = ctx.DeviceId, EmployeeId = ctx.EmployeeId, EventType = ctx.EventType,
            DeviceType = Text.Trunc(p.DeviceType, 64)!, VendorId = Text.Trunc(p.VendorId, 16), ProductId = Text.Trunc(p.ProductId, 16),
            Vendor = Text.Trunc(p.Vendor, 256), Product = Text.Trunc(p.Product, 256), SerialNumber = Text.Trunc(p.SerialNumber, 256),
            InstanceId = Text.Trunc(p.InstanceId, 512), DriveLetter = Text.Trunc(p.DriveLetter, 8), Timestamp = ctx.Timestamp,
        });
        return Task.CompletedTask;
    }
}

/// <summary>FILE_*: metadata only.</summary>
internal sealed class FileProjector(IAppDbContext db) : IEventProjector
{
    public IReadOnlyCollection<string> EventTypes { get; } =
    [
        ET.FileCreated, ET.FileModified,
        ET.FileRenamed, ET.FileDeleted,
    ];

    public Task ProjectAsync(ProjectionContext ctx, CancellationToken ct)
    {
        var p = ctx.Payload<FilePayload>();
        var path = Text.Trunc(p.Path, 2000)!;
        var name = string.IsNullOrWhiteSpace(p.FileName) ? System.IO.Path.GetFileName(path) : p.FileName;
        db.FileEvents.Add(new FileEvent
        {
            Id = ctx.Envelope.EventId, DeviceId = ctx.DeviceId, EmployeeId = ctx.EmployeeId,
            Action = ctx.EventType["FILE_".Length..], Path = path, OldPath = Text.Trunc(p.OldPath, 2000),
            FileName = Text.Trunc(name, 512) ?? "", Extension = Text.Trunc(p.Extension, 32), UserName = Text.Trunc(p.UserName, 256), Timestamp = ctx.Timestamp,
        });
        return Task.CompletedTask;
    }
}

/// <summary>SYSTEM_METRIC: <c>system_metrics</c> row, live gauges, device network identifiers and boot time.</summary>
internal sealed class SystemMetricProjector(IAppDbContext db, ILiveStateWriter live) : IEventProjector
{
    public IReadOnlyCollection<string> EventTypes { get; } = [ET.SystemMetric];

    public async Task ProjectAsync(ProjectionContext ctx, CancellationToken ct)
    {
        var p = ctx.Payload<SystemMetricPayload>();
        var disks = p.Disks ?? [];
        var disk = disks.FirstOrDefault(d => d.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase)) ?? disks.FirstOrDefault();
        db.SystemMetrics.Add(new SystemMetric
        {
            Id = ctx.Envelope.EventId, DeviceId = ctx.DeviceId, Timestamp = ctx.Timestamp,
            CpuPercent = p.CpuPercent, MemoryUsedPercent = p.MemoryUsedPercent, MemoryTotalMb = p.MemoryTotalMb, MemoryAvailableMb = p.MemoryAvailableMb,
            DiskUsedPercent = disk?.UsedPercent, DiskFreeGb = disk?.FreeGb, NetworkConnected = p.NetworkConnected,
            BatteryPercent = p.Battery is { Present: true } b ? b.Percent : null, UptimeSeconds = p.UptimeSeconds,
            DisksJson = JsonColumn.Serialize(disks),
        });

        var d = ctx.Device;
        if (p.IpAddresses is { Count: > 0 }) d.IpAddressesJson = JsonColumn.Serialize(p.IpAddresses);
        if (p.MacAddresses is { Count: > 0 }) d.MacAddressesJson = JsonColumn.Serialize(p.MacAddresses);
        if (p.UptimeSeconds > 0) d.LastBootAt = ctx.Timestamp.AddSeconds(-p.UptimeSeconds);

        await live.SetMetricsAsync(ctx.DeviceId, p.CpuPercent, p.MemoryUsedPercent, disk?.UsedPercent, p.NetworkConnected, ctx.Timestamp, ct);
    }
}

/// <summary>NETWORK_CONNECTED / NETWORK_DISCONNECTED: live state only (the raw event stays in <c>event_ingestion</c>).</summary>
internal sealed class NetworkProjector(ILiveStateWriter live) : IEventProjector
{
    public IReadOnlyCollection<string> EventTypes { get; } = [ET.NetworkConnected, ET.NetworkDisconnected];

    public Task ProjectAsync(ProjectionContext ctx, CancellationToken ct) =>
        live.SetNetworkAsync(ctx.DeviceId, ctx.EventType == ET.NetworkConnected, ctx.Timestamp, ct);
}

/// <summary>AGENT_* lifecycle events.</summary>
internal sealed class AgentEventProjector(
    ILiveStateWriter live, IDeviceStatusService statusService, ISecurityEventService security, Updates.IAgentUpdateService updates) : IEventProjector
{
    public IReadOnlyCollection<string> EventTypes { get; } =
    [
        ET.AgentStarted, ET.AgentStopped, ET.AgentError,
        ET.AgentVersionChanged, ET.AgentHeartbeat,
    ];

    public async Task ProjectAsync(ProjectionContext ctx, CancellationToken ct)
    {
        var p = ctx.Payload<AgentPayload>();
        var device = ctx.Device;
        var dref = new DeviceRef(device.Id, device.DeviceCode);
        var status = await live.GetOrCreateAsync(device.Id, ct);

        switch (ctx.EventType)
        {
            case ET.AgentStarted:
                status.ServiceState = "Running";
                if (!string.IsNullOrWhiteSpace(p.AgentVersion)) device.AgentVersion = Text.Trunc(p.AgentVersion, 64)!;
                if (p.UnexpectedPreviousShutdown == true)
                {
                    var who = string.IsNullOrWhiteSpace(status.UserName) ? "" : $" (last signed-in user: {status.UserName})";
                    await security.RaiseAsync(dref, SecurityEventKinds.UnexpectedShutdown, SecuritySeverity.Warning,
                        $"{device.DeviceCode}: the agent did not shut down cleanly before this start (possible kill/crash/power loss).{who}", onlyIfNoneOpen: false, throttle: TimeSpan.FromMinutes(10), ct: ct);
                }
                break;

            case ET.AgentStopped:
                status.ServiceState = "Stopped";
                // A stop older than the last heartbeat has already been superseded by that heartbeat.
                if ((device.LastSeen is null || ctx.Timestamp >= device.LastSeen) && !HealthEvaluator.IsAdminState(device.Status))
                {
                    device.ServiceStoppedAt = device.ServiceStoppedAt is { } prev && prev > ctx.Timestamp ? prev : ctx.Timestamp;
                    var deliberate = !string.IsNullOrWhiteSpace(p.InteractiveUser);
                    // SERVER-AUTHORITATIVE: only the server's own rollout state may excuse a deliberate stop. Nothing the
                    // agent reports (reason text, a marker) can suppress the alert - a local admin could forge that.
                    var updateRestart = deliberate && await updates.HasAssignedInProgressUpdateAsync(device.Id, ct);
                    await statusService.ApplyAsync(device, DeviceHealth.ServiceStopped, updateRestart ? StopReasons.UpdateRestart : p.Reason ?? "AGENT_STOPPED", ct);
                    // Deliberate stop while a user was signed in (the agent only sets InteractiveUser then). Raise a
                    // Critical, attributed tamper alert that is NOT auto-resolved - so the watchdog restarting the
                    // service seconds later cannot silently clear it; a human must acknowledge it.
                    if (deliberate && !updateRestart)
                        await security.RaiseAsync(dref, SecurityEventKinds.TamperSuspected, SecuritySeverity.Critical,
                            $"{device.DeviceCode} ({device.Hostname}): monitoring service was stopped while {p.InteractiveUser} was signed in.",
                            onlyIfNoneOpen: false, throttle: TimeSpan.FromMinutes(1), ct: ct);
                }
                break;

            case ET.AgentError:
                status.LastError = Text.Trunc(p.Message ?? p.Reason, 1000);
                await security.RaiseAsync(dref, SecurityEventKinds.CollectorFailure, SecuritySeverity.Warning,
                    $"{device.DeviceCode}: {p.Component ?? "agent"} reported an error: {Text.Trunc(p.Message ?? p.Reason, 300)}",
                    onlyIfNoneOpen: false, throttle: TimeSpan.FromMinutes(5), ct: ct);
                break;

            case ET.AgentVersionChanged:
                if (!string.IsNullOrWhiteSpace(p.AgentVersion)) device.AgentVersion = Text.Trunc(p.AgentVersion, 64)!;
                break;
        }
    }
}
