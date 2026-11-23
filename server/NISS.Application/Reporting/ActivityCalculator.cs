using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Monitor.Protocol;

namespace NISS.Application.Reporting;

/// <summary>Logged-in time split into active, idle and locked/disconnected time. Categories never overlap.</summary>
public sealed record PresenceBreakdown(
    IReadOnlyList<Interval> LoggedIn, IReadOnlyList<Interval> Active, IReadOnlyList<Interval> Idle, IReadOnlyList<Interval> Locked)
{
    public double LoggedInSeconds => IntervalMath.TotalSeconds(LoggedIn);
    public double ActiveSeconds => IntervalMath.TotalSeconds(Active);
    public double IdleSeconds => IntervalMath.TotalSeconds(Idle);
    public double LockedSeconds => IntervalMath.TotalSeconds(Locked);

    public static PresenceBreakdown Empty { get; } = new([], [], [], []);
}

/// <summary>One logon row with the last-seen time of its device (used to cap sessions that were never closed).</summary>
public sealed record LoginRow(Guid DeviceId, Guid? EmployeeId, DateTimeOffset Login, DateTimeOffset? Logout, DateTimeOffset? DeviceLastSeen);

/// <summary>One non-active period row.</summary>
public sealed record IdleRow(Guid DeviceId, Guid? EmployeeId, string Kind, DateTimeOffset Start, DateTimeOffset? End, DateTimeOffset? DeviceLastSeen);

/// <summary>All logon and idle rows overlapping a window.</summary>
public sealed record ActivityRows(IReadOnlyList<LoginRow> Logins, IReadOnlyList<IdleRow> Idles, DateTimeOffset Now);

/// <summary>Pure computation: active = logged-in minus (locked, disconnected and idle) intervals, clipped to the window.</summary>
public static class PresenceCalculator
{
    public static PresenceBreakdown Compute(IEnumerable<LoginRow> logins, IEnumerable<IdleRow> idles, TimeRange window, DateTimeOffset now)
    {
        var win = new Interval(window.From, window.To);
        var loggedIn = IntervalMath.Clip(logins.Select(l => new Interval(l.Login, OpenEnd(l.Login, l.Logout, l.DeviceLastSeen, now))), win);

        var idleList = idles.ToList();
        var lockedRaw = idleList.Where(i => i.Kind is IdleKinds.Locked or IdleKinds.Disconnected)
            .Select(i => new Interval(i.Start, OpenEnd(i.Start, i.End, i.DeviceLastSeen, now)));
        var idleRaw = idleList.Where(i => i.Kind == IdleKinds.Idle)
            .Select(i => new Interval(i.Start, OpenEnd(i.Start, i.End, i.DeviceLastSeen, now)));

        var locked = IntervalMath.Intersect(loggedIn, IntervalMath.Clip(lockedRaw, win));
        var idle = IntervalMath.Subtract(IntervalMath.Intersect(loggedIn, IntervalMath.Clip(idleRaw, win)), locked);
        var active = IntervalMath.Subtract(IntervalMath.Subtract(loggedIn, locked), idle);
        return new PresenceBreakdown(loggedIn, active, idle, locked);
    }

    /// <summary>An unclosed interval ends now, but never later than the last time the device was heard from.</summary>
    private static DateTimeOffset OpenEnd(DateTimeOffset start, DateTimeOffset? end, DateTimeOffset? lastSeen, DateTimeOffset now)
    {
        if (end is { } e) return e;
        return lastSeen is { } seen && seen > start && seen < now ? seen : now;
    }
}

/// <summary>Loads the rows needed by <see cref="PresenceCalculator"/>.</summary>
public interface IActivityLoader
{
    Task<ActivityRows> LoadAsync(Scope scope, TimeRange window, CancellationToken ct);
}

internal sealed class ActivityLoader(IAppDbContext db, TimeProvider clock) : IActivityLoader
{
    public async Task<ActivityRows> LoadAsync(Scope scope, TimeRange window, CancellationToken ct)
    {
        var from = window.From; var to = window.To;
        var logins = db.UserSessions.AsNoTracking().InScope(scope).Where(s => s.LoginAt < to && (s.LogoutAt == null || s.LogoutAt > from));
        var idles = db.IdleSessions.AsNoTracking().InScope(scope).Where(s => s.StartedAt < to && (s.EndedAt == null || s.EndedAt > from));

        var loginRows = await (from s in logins
                               join d in db.Devices.AsNoTracking() on s.DeviceId equals d.Id
                               select new LoginRow(s.DeviceId, s.EmployeeId, s.LoginAt, s.LogoutAt, d.LastSeen)).ToListAsync(ct);
        var idleRows = await (from s in idles
                              join d in db.Devices.AsNoTracking() on s.DeviceId equals d.Id
                              select new IdleRow(s.DeviceId, s.EmployeeId, s.Kind, s.StartedAt, s.EndedAt, d.LastSeen)).ToListAsync(ct);
        return new ActivityRows(loginRows, idleRows, clock.GetUtcNow());
    }
}
