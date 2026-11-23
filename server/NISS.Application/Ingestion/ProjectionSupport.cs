using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Ingestion;

/// <summary>Everything a projector needs to apply one accepted event.</summary>
public sealed record ProjectionContext(Device Device, Guid? EmployeeId, EventEnvelope Envelope, DateTimeOffset Timestamp, DateTimeOffset ReceivedAt)
{
    public Guid DeviceId => Device.Id;
    public string EventType => Envelope.EventType;

    /// <summary>Typed payload; validated before projection so this never returns null for accepted events.</summary>
    public T Payload<T>() where T : class =>
        ProtocolJson.FromElement<T>(Envelope.Payload) ?? throw new InvalidOperationException($"Payload of {EventType} does not match {typeof(T).Name}.");
}

/// <summary>Applies one family of events to the query tables. Must be order tolerant and idempotent.</summary>
public interface IEventProjector
{
    IReadOnlyCollection<string> EventTypes { get; }
    Task ProjectAsync(ProjectionContext ctx, CancellationToken ct);
}

/// <summary>Maps windows accounts and employee hints to employees and remembers the mapping.</summary>
public interface IEmployeeResolver
{
    /// <summary>Employee for an event: device employee, then the employee code hint, then the windows user mapping.</summary>
    Task<Guid?> ResolveAsync(Device device, string? employeeCodeHint, string? userName, CancellationToken ct);
    Task EnsureDeviceUserAsync(Device device, string? userName, Guid? employeeId, DateTimeOffset seenAt, CancellationToken ct);
    Task<string?> CodeAsync(Guid? employeeId, CancellationToken ct);
}

internal sealed class EmployeeResolver(IAppDbContext db) : IEmployeeResolver
{
    private readonly Dictionary<string, Guid?> _codes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(Guid, string), Guid?> _users = [];
    private readonly Dictionary<Guid, string?> _byId = [];

    public async Task<Guid?> ResolveAsync(Device device, string? employeeCodeHint, string? userName, CancellationToken ct)
    {
        if (device.EmployeeId is { } id) return id;

        if (!string.IsNullOrWhiteSpace(employeeCodeHint))
        {
            var code = employeeCodeHint.Trim();
            if (!_codes.TryGetValue(code, out var byCode))
            {
                byCode = await db.Employees.AsNoTracking().Where(e => e.EmployeeCode.ToLower() == code.ToLower()).Select(e => (Guid?)e.Id).FirstOrDefaultAsync(ct);
                _codes[code] = byCode;
            }
            if (byCode is not null) return byCode;
        }

        var user = (userName ?? "").Trim();
        var key = (device.Id, user.ToLowerInvariant());
        if (!_users.TryGetValue(key, out var mapped))
        {
            var deviceId = device.Id;
            if (user.Length > 0)
                mapped = await db.DeviceUsers.AsNoTracking()
                    .Where(u => u.DeviceId == deviceId && u.WindowsUsername.ToLower() == user.ToLower() && u.EmployeeId != null)
                    .Select(u => u.EmployeeId).FirstOrDefaultAsync(ct);
            if (mapped is null)
            {
                var all = await db.DeviceUsers.AsNoTracking().Where(u => u.DeviceId == deviceId && u.EmployeeId != null)
                    .Select(u => u.EmployeeId!.Value).Distinct().Take(2).ToListAsync(ct);
                mapped = all.Count == 1 ? all[0] : null;
            }
            _users[key] = mapped;
        }
        return mapped;
    }

    public async Task EnsureDeviceUserAsync(Device device, string? userName, Guid? employeeId, DateTimeOffset seenAt, CancellationToken ct)
    {
        var name = (userName ?? "").Trim();
        if (name.Length == 0) return;
        var deviceId = device.Id;
        var row = await db.DeviceUsers.FirstOrDefaultAsync(u => u.DeviceId == deviceId && u.WindowsUsername.ToLower() == name.ToLower(), ct);
        if (row is null)
        {
            db.DeviceUsers.Add(new DeviceUser
            {
                Id = Guid.NewGuid(), DeviceId = deviceId, WindowsUsername = name, EmployeeId = employeeId, FirstSeen = seenAt, LastSeen = seenAt,
            });
        }
        else
        {
            if (seenAt > row.LastSeen) row.LastSeen = seenAt;
            if (row.EmployeeId is null && employeeId is not null) row.EmployeeId = employeeId;
        }
        _users.Remove((deviceId, name.ToLowerInvariant()));
    }

    public async Task<string?> CodeAsync(Guid? employeeId, CancellationToken ct)
    {
        if (employeeId is not { } id) return null;
        if (_byId.TryGetValue(id, out var cached)) return cached;
        var code = await db.Employees.AsNoTracking().Where(e => e.Id == id).Select(e => e.EmployeeCode).FirstOrDefaultAsync(ct);
        _byId[id] = code;
        return code;
    }
}

/// <summary>Writes the "live" columns of <c>agent_status</c>, ignoring information older than what is already stored.</summary>
public interface ILiveStateWriter
{
    Task<AgentStatus> GetOrCreateAsync(Guid deviceId, CancellationToken ct);
    Task SetPresenceAsync(Guid deviceId, string presence, DateTimeOffset at, string? userName, bool clearUser, CancellationToken ct);
    Task SetForegroundAsync(Guid deviceId, int processId, string processName, string? path, string? window, DateTimeOffset at, CancellationToken ct);
    Task ClearForegroundAsync(Guid deviceId, int processId, DateTimeOffset at, CancellationToken ct);
    Task SetNetworkAsync(Guid deviceId, bool connected, DateTimeOffset at, CancellationToken ct);
    Task SetMetricsAsync(Guid deviceId, double cpu, double memory, double? disk, bool network, DateTimeOffset at, CancellationToken ct);
}

internal sealed class LiveStateWriter(IAppDbContext db, TimeProvider clock) : ILiveStateWriter
{
    public async Task<AgentStatus> GetOrCreateAsync(Guid deviceId, CancellationToken ct)
    {
        var row = await db.AgentStatuses.FindAsync([deviceId], ct);
        if (row is not null) return row;
        row = new AgentStatus { DeviceId = deviceId, LiveUpdatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = clock.GetUtcNow() };
        db.AgentStatuses.Add(row);
        return row;
    }

    private async Task<AgentStatus?> FreshAsync(Guid deviceId, DateTimeOffset at, CancellationToken ct)
    {
        var row = await GetOrCreateAsync(deviceId, ct);
        if (at < row.LiveUpdatedAt) return null;
        row.LiveUpdatedAt = at;
        row.UpdatedAt = clock.GetUtcNow();
        return row;
    }

    public async Task SetPresenceAsync(Guid deviceId, string presence, DateTimeOffset at, string? userName, bool clearUser, CancellationToken ct)
    {
        if (await FreshAsync(deviceId, at, ct) is not { } s) return;
        s.Presence = presence;
        if (clearUser) { s.UserName = null; s.CurrentApplication = null; s.CurrentProcessId = null; s.CurrentExecutablePath = null; s.CurrentWindow = null; s.Interacting = false; }
        else if (!string.IsNullOrWhiteSpace(userName)) s.UserName = userName;
        if (presence is Presence.Idle or Presence.Locked or Presence.Disconnected) s.Interacting = false;
    }

    public async Task SetForegroundAsync(Guid deviceId, int processId, string processName, string? path, string? window, DateTimeOffset at, CancellationToken ct)
    {
        if (await FreshAsync(deviceId, at, ct) is not { } s) return;
        s.CurrentProcessId = processId;
        s.CurrentApplication = processName;
        s.CurrentExecutablePath = path;
        s.CurrentWindow = window;
    }

    public async Task ClearForegroundAsync(Guid deviceId, int processId, DateTimeOffset at, CancellationToken ct)
    {
        var current = await GetOrCreateAsync(deviceId, ct);
        if (current.CurrentProcessId != processId || at < current.LiveUpdatedAt) return;
        current.LiveUpdatedAt = at;
        current.CurrentProcessId = null; current.CurrentApplication = null; current.CurrentExecutablePath = null; current.CurrentWindow = null;
    }

    public async Task SetNetworkAsync(Guid deviceId, bool connected, DateTimeOffset at, CancellationToken ct)
    {
        if (await FreshAsync(deviceId, at, ct) is not { } s) return;
        s.NetworkConnected = connected;
    }

    public async Task SetMetricsAsync(Guid deviceId, double cpu, double memory, double? disk, bool network, DateTimeOffset at, CancellationToken ct)
    {
        if (await FreshAsync(deviceId, at, ct) is not { } s) return;
        s.CpuPercent = cpu; s.MemoryUsedPercent = memory; s.DiskUsedPercent = disk; s.NetworkConnected = network;
    }
}
