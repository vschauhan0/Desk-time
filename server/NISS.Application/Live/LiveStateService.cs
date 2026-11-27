using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Live;

/// <summary>Filter for the live monitoring grid.</summary>
public sealed record LiveFilter(string? Status, string? Department, string? Employee, string? Search);

/// <summary>Builds and publishes <see cref="LiveDeviceCard"/>s.</summary>
public interface ILiveStateService
{
    Task<LiveDeviceCard?> GetCardAsync(Guid deviceId, CancellationToken ct);
    Task<IReadOnlyList<LiveDeviceCard>> QueryAsync(LiveFilter filter, CancellationToken ct);

    /// <summary>Builds a fresh card and pushes it as <c>DeviceStateChanged</c>. Never throws.</summary>
    Task PublishAsync(Guid deviceId, CancellationToken ct);
}

/// <summary>Pure mapping from stored rows to the live card.</summary>
public static class LiveCardFactory
{
    private static readonly HashSet<string> LivePresenceStatuses = [DeviceHealth.Online, DeviceHealth.Warning];

    public static LiveDeviceCard Build(Device d, Employee? e, AgentStatus? s)
    {
        var presence = s is not null && LivePresenceStatuses.Contains(d.Status) ? s.Presence : Presence.Unknown;
        return new LiveDeviceCard(
            d.DeviceCode, d.Hostname, e?.EmployeeCode, e?.FullName, e?.Department, d.Status, presence,
            IsIdle: presence == Presence.Idle,
            IsLocked: presence == Presence.Locked,
            IsInteracting: s?.Interacting == true && presence == Presence.Active,
            CurrentApplication: s?.CurrentApplication, CurrentWindow: s?.CurrentWindow,
            Applications: BuildApps(s, presence),
            CpuPercent: s?.CpuPercent, MemoryUsedPercent: s?.MemoryUsedPercent, NetworkConnected: s?.NetworkConnected,
            LastHeartbeatUtc: s?.LastHeartbeatAt ?? d.LastSeen, AgentVersion: d.AgentVersion, UserName: s?.UserName);
    }

    /// <summary>Running = every open app; Foreground = only the foreground process; Interaction = Foreground and the user is interacting.</summary>
    public static IReadOnlyList<AppState> BuildApps(AgentStatus? s, string presence)
    {
        if (s is null) return [];
        var open = JsonColumn.DeserializeList<OpenApplication>(s.OpenApplicationsJson);
        var interacting = s.Interacting && presence == Presence.Active;
        var result = new List<AppState>(open.Count + 1);
        var foregroundSeen = false;
        foreach (var app in open)
        {
            var fg = IsForeground(s, app.ProcessId, app.ProcessName);
            foregroundSeen |= fg;
            result.Add(new AppState(app.ProcessName, app.ProcessId, app.WindowTitle, app.StartedUtc, true, fg, fg && interacting));
        }
        if (!foregroundSeen && !string.IsNullOrEmpty(s.CurrentApplication))
            result.Add(new AppState(s.CurrentApplication, s.CurrentProcessId ?? 0, s.CurrentWindow, null, true, true, interacting));
        return result;
    }

    private static bool IsForeground(AgentStatus s, int pid, string name)
    {
        if (string.IsNullOrEmpty(s.CurrentApplication)) return false;
        if (s.CurrentProcessId is { } cur) return cur == pid && string.Equals(s.CurrentApplication, name, StringComparison.OrdinalIgnoreCase);
        return string.Equals(s.CurrentApplication, name, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class LiveStateService(
    IAppDbContext db, IRealtimeNotifier notifier, Auth.IAccessScopeProvider access, Microsoft.Extensions.Logging.ILogger<LiveStateService> log) : ILiveStateService
{
    private sealed class LiveRow { public Device D { get; set; } = null!; public Employee? E { get; set; } public AgentStatus? S { get; set; } }

    private IQueryable<LiveRow> Joined() =>
        from d in db.Devices.AsNoTracking()
        join e in db.Employees.AsNoTracking() on d.EmployeeId equals e.Id into eg
        from e in eg.DefaultIfEmpty()
        join s in db.AgentStatuses.AsNoTracking() on d.Id equals s.DeviceId into sg
        from s in sg.DefaultIfEmpty()
        select new LiveRow { D = d, E = e, S = s };

    public async Task<LiveDeviceCard?> GetCardAsync(Guid deviceId, CancellationToken ct)
    {
        // RBAC: a restricted user cannot read a device outside their scope.
        if (!(await access.CurrentAsync(ct)).CanSeeDevice(deviceId)) return null;
        var row = await Joined().Where(x => x.D.Id == deviceId).FirstOrDefaultAsync(ct);
        return row is null ? null : LiveCardFactory.Build(row.D, row.E, row.S);
    }

    public async Task<IReadOnlyList<LiveDeviceCard>> QueryAsync(LiveFilter f, CancellationToken ct)
    {
        var q = Joined();
        var scope = await access.CurrentAsync(ct);
        if (!scope.All)
        {
            var allowed = scope.DeviceIds.ToList();
            q = q.Where(x => allowed.Contains(x.D.Id));
        }
        if (!string.IsNullOrWhiteSpace(f.Status)) { var st = f.Status.Trim().ToUpperInvariant(); q = q.Where(x => x.D.Status == st); }
        if (!string.IsNullOrWhiteSpace(f.Department)) { var dept = f.Department.Trim(); q = q.Where(x => x.E != null && x.E.Department == dept); }
        if (!string.IsNullOrWhiteSpace(f.Employee)) { var code = f.Employee.Trim(); q = q.Where(x => x.E != null && x.E.EmployeeCode == code); }
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var like = QueryExtensions.LikeContains(f.Search.Trim());
            q = q.Where(x => EF.Functions.Like(x.D.DeviceCode.ToLower(), like) || EF.Functions.Like(x.D.Hostname.ToLower(), like)
                || (x.E != null && (EF.Functions.Like(x.E.FullName.ToLower(), like) || EF.Functions.Like(x.E.EmployeeCode.ToLower(), like)))
                || (x.S != null && x.S.CurrentApplication != null && EF.Functions.Like(x.S.CurrentApplication.ToLower(), like)));
        }
        var rows = await q.OrderBy(x => x.D.DeviceCode).ToListAsync(ct);
        return rows.Select(r => LiveCardFactory.Build(r.D, r.E, r.S)).ToList();
    }

    public async Task PublishAsync(Guid deviceId, CancellationToken ct)
    {
        try
        {
            var card = await GetCardAsync(deviceId, ct);
            if (card is not null) await notifier.DeviceStateChangedAsync(card, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Publishing DeviceStateChanged failed for device {DeviceId}", deviceId);
        }
    }
}
