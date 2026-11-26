using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NISS.Application.Abstractions;
using NISS.Application.Audit;
using NISS.Application.Common;
using NISS.Application.Options;
using NISS.Application.Policies;
using NISS.Monitor.Protocol;

namespace NISS.Application.Live;

/// <summary>An in-flight live-view session.</summary>
public sealed record LiveSessionInfo(Guid Id, Guid DeviceId, string DeviceCode, int Monitor, DateTimeOffset StartedAt);

/// <summary>Singleton registry of active live-view sessions (shared across requests and the auto-stop timer).</summary>
public sealed class LiveSessionRegistry
{
    private readonly ConcurrentDictionary<Guid, LiveSessionInfo> _sessions = new();

    public void Add(LiveSessionInfo session) => _sessions[session.Id] = session;
    public LiveSessionInfo? Remove(Guid id) => _sessions.TryRemove(id, out var s) ? s : null;

    public LiveSessionInfo? Update(Guid id, int monitor)
    {
        if (!_sessions.TryGetValue(id, out var s)) return null;
        var updated = s with { Monitor = monitor };
        _sessions[id] = updated;
        return updated;
    }
}

/// <summary>Starts/stops/switches live screen view for a device (SUPERADMIN). Enforces policy, reachability, audit and a session cap.</summary>
public interface ILiveViewService
{
    Task<LiveViewSessionDto> StartAsync(string deviceCode, int monitor, CancellationToken ct);
    Task StopAsync(Guid liveSessionId, CancellationToken ct);
    Task SwitchAsync(Guid liveSessionId, int monitor, CancellationToken ct);
}

internal sealed class LiveViewService(
    IAppDbContext db,
    IPolicyService policies,
    ILiveRelay relay,
    IAuditService audit,
    LiveSessionRegistry registry,
    IOptions<ScreenLiveOptions> live,
    TimeProvider clock) : ILiveViewService
{
    public async Task<LiveViewSessionDto> StartAsync(string deviceCode, int monitor, CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceCode == deviceCode, ct)
            ?? throw new NotFoundException("Device not found.");

        var dept = device.EmployeeId is { } id
            ? await db.Employees.Where(e => e.Id == id).Select(e => e.Department).FirstOrDefaultAsync(ct)
            : null;
        var policy = await policies.GetEffectiveAsync(device.DeviceCode, dept, ct);
        if (!policy.ScreenCapture.LiveEnabled)
            throw new ConflictException("Live view is disabled by policy for this device.");
        if (!relay.IsDeviceConnected(device.Id))
            throw new ConflictException("Device is not reachable for live view (offline or no interactive session).");

        var liveSessionId = Guid.NewGuid();
        var opts = live.Value;
        var fps = Math.Clamp(opts.MaxFps, 1, 15);
        var sent = await relay.SendControlAsync(device.Id,
            new LiveControlMessage(LiveControlAction.Start, liveSessionId, monitor, fps, 50), ct);
        if (!sent) throw new ConflictException("Device is not reachable for live view.");

        var info = new LiveSessionInfo(liveSessionId, device.Id, device.DeviceCode, monitor, clock.GetUtcNow());
        registry.Add(info);
        await audit.WriteAsync(AuditActions.LiveViewStarted, device.DeviceCode, $"monitor {monitor}", ct);
        ScheduleAutoStop(liveSessionId, device.Id, TimeSpan.FromMinutes(Math.Clamp(opts.MaxSessionMinutes, 1, 240)));
        return new LiveViewSessionDto(liveSessionId, device.DeviceCode, $"live:{liveSessionId}", monitor, info.StartedAt);
    }

    public async Task StopAsync(Guid liveSessionId, CancellationToken ct)
    {
        var info = registry.Remove(liveSessionId);
        if (info is null) return; // idempotent
        await relay.SendControlAsync(info.DeviceId, new LiveControlMessage(LiveControlAction.Stop, liveSessionId, info.Monitor, 0, 0), ct);
        var duration = (int)(clock.GetUtcNow() - info.StartedAt).TotalSeconds;
        await audit.WriteAsync(AuditActions.LiveViewEnded, info.DeviceCode, $"monitor {info.Monitor}, {duration}s", ct);
    }

    public async Task SwitchAsync(Guid liveSessionId, int monitor, CancellationToken ct)
    {
        var info = registry.Update(liveSessionId, monitor) ?? throw new NotFoundException("Live session not found.");
        await relay.SendControlAsync(info.DeviceId, new LiveControlMessage(LiveControlAction.Switch, liveSessionId, monitor, 0, 0), ct);
    }

    /// <summary>Backstop: force-stop the session after the cap even if no explicit stop arrives. Not audited.</summary>
    private void ScheduleAutoStop(Guid liveSessionId, Guid deviceId, TimeSpan cap)
    {
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(cap); } catch { return; }
            var info = registry.Remove(liveSessionId);
            if (info is null) return;
            try { await relay.SendControlAsync(deviceId, new LiveControlMessage(LiveControlAction.Stop, liveSessionId, info.Monitor, 0, 0), CancellationToken.None); }
            catch { /* best effort */ }
        });
    }
}
