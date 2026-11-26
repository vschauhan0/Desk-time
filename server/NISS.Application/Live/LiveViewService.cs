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

/// <summary>
/// Singleton registry of active live-view sessions, shared across requests, the SignalR hub, the frame socket and the
/// auto-stop timer. Tracks one session per device (concurrent watchers share it) and who is watching each session so a
/// session can be torn down when its last viewer leaves.
/// </summary>
public sealed class LiveSessionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, LiveSessionInfo> _byId = new();
    private readonly Dictionary<Guid, Guid> _deviceToSession = new();
    private readonly Dictionary<Guid, HashSet<string>> _viewers = new();

    public void Add(LiveSessionInfo session)
    {
        lock (_gate)
        {
            _byId[session.Id] = session;
            _deviceToSession[session.DeviceId] = session.Id;
            _viewers.TryAdd(session.Id, new HashSet<string>());
        }
    }

    /// <summary>The device's current active session, if any (so a second watcher shares it rather than starting a new one).</summary>
    public LiveSessionInfo? GetByDevice(Guid deviceId)
    {
        lock (_gate)
            return _deviceToSession.TryGetValue(deviceId, out var id) && _byId.TryGetValue(id, out var s) ? s : null;
    }

    /// <summary>True when <paramref name="sessionId"/> exists and belongs to <paramref name="deviceId"/> (frame-injection guard).</summary>
    public bool Owns(Guid sessionId, Guid deviceId)
    {
        lock (_gate)
            return _byId.TryGetValue(sessionId, out var s) && s.DeviceId == deviceId;
    }

    public LiveSessionInfo? Remove(Guid id)
    {
        lock (_gate)
        {
            if (!_byId.Remove(id, out var s)) return null;
            if (_deviceToSession.TryGetValue(s.DeviceId, out var sid) && sid == id) _deviceToSession.Remove(s.DeviceId);
            _viewers.Remove(id);
            return s;
        }
    }

    public LiveSessionInfo? Update(Guid id, int monitor)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var s)) return null;
            var updated = s with { Monitor = monitor };
            _byId[id] = updated;
            return updated;
        }
    }

    public void AddViewer(Guid sessionId, string connectionId)
    {
        lock (_gate)
        {
            if (!_viewers.TryGetValue(sessionId, out var set)) { set = new HashSet<string>(); _viewers[sessionId] = set; }
            set.Add(connectionId);
        }
    }

    /// <summary>Removes a viewer; returns the number of viewers that remain for the session.</summary>
    public int RemoveViewer(Guid sessionId, string connectionId)
    {
        lock (_gate)
            return _viewers.TryGetValue(sessionId, out var set) && set.Remove(connectionId) ? set.Count : (_viewers.TryGetValue(sessionId, out var s2) ? s2.Count : 0);
    }

    /// <summary>Sessions this connection is currently watching (for teardown on disconnect).</summary>
    public IReadOnlyList<Guid> SessionsWatchedBy(string connectionId)
    {
        lock (_gate)
            return _viewers.Where(kv => kv.Value.Contains(connectionId)).Select(kv => kv.Key).ToList();
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

        // Concurrent watchers share one session per device: return the active one rather than starting a second stream.
        var active = registry.GetByDevice(device.Id);
        if (active is not null)
            return new LiveViewSessionDto(active.Id, device.DeviceCode, $"live:{active.Id}", active.Monitor, active.StartedAt);

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
        const int liveQuality = 72; // sharper live image; still well within the frame-size budget
        var sent = await relay.SendControlAsync(device.Id,
            new LiveControlMessage(LiveControlAction.Start, liveSessionId, monitor, fps, liveQuality), ct);
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
