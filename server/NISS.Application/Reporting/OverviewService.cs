using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Application.Health;
using NISS.Monitor.Protocol;

namespace NISS.Application.Reporting;

/// <summary>Dashboard landing page numbers.</summary>
public interface IOverviewService
{
    Task<OverviewDto> GetAsync(CancellationToken ct);
}

internal sealed class OverviewService(
    IAppDbContext db, TimeProvider clock, IActivityLoader activity, IApplicationUsageService usage, ISecurityEventService security,
    Auth.IAccessScopeProvider access) : IOverviewService
{
    public async Task<OverviewDto> GetAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var offset = TimeRange.ServerOffsetMinutes(now, clock);
        var today = TimeRange.ForDay(DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromMinutes(offset)).DateTime), offset);

        // RBAC: the whole landing page reflects only the devices/employees this user may see.
        var access_ = await access.CurrentAsync(ct);
        var scope = access_.All ? Scope.All : new Scope(access_.DeviceIds.ToList());
        List<Guid>? allowedDevices = access_.All ? null : access_.DeviceIds.ToList();

        var devicesQuery = db.Devices.AsNoTracking().AsQueryable();
        if (allowedDevices is not null) devicesQuery = devicesQuery.Where(d => allowedDevices.Contains(d.Id));
        var devices = await (from d in devicesQuery
                             join s in db.AgentStatuses.AsNoTracking() on d.Id equals s.DeviceId into sg
                             from s in sg.DefaultIfEmpty()
                             select new { d.Status, d.AgentVersion, Presence = s == null ? null : s.Presence }).ToListAsync(ct);
        var live = devices.Where(d => d.Status is DeviceHealth.Online or DeviceHealth.Warning).ToList();

        var from = today.From; var to = today.To;
        var eventsQuery = db.EventIngestions.AsNoTracking().Where(e => e.Timestamp >= from && e.Timestamp < to);
        if (allowedDevices is not null) eventsQuery = eventsQuery.Where(e => allowedDevices.Contains(e.DeviceId));
        var eventsToday = await eventsQuery.LongCountAsync(ct);
        var alertsQuery = db.SecurityEvents.AsNoTracking().Where(e => e.ResolvedAt == null);
        if (allowedDevices is not null) alertsQuery = alertsQuery.Where(e => e.DeviceId != null && allowedDevices.Contains(e.DeviceId.Value));
        var openAlerts = await alertsQuery.CountAsync(ct);
        var totalEmployees = access_.All
            ? await db.Employees.AsNoTracking().CountAsync(ct)
            : access_.EmployeeIds.Count;

        var top = (await usage.GetTotalsAsync(new UsageFilter(scope), today, ct)).Take(8).ToList();
        var recent = await security.QueryAsync(new SecurityEventFilter(null, null, null, null, null, null), new PageRequest(1, 8), ct);

        // Flagged-activity summary for the dashboard (counts + site names only; the images stay SUPERADMIN-gated).
        var flaggedQuery = db.Screenshots.AsNoTracking()
            .Where(s => s.Trigger == ScreenTriggers.Flagged && (s.ClipId == null || s.ClipSeq == 0));
        if (allowedDevices is not null) flaggedQuery = flaggedQuery.Where(s => allowedDevices.Contains(s.DeviceId));
        var flaggedToday = await flaggedQuery.CountAsync(s => s.CapturedAtUtc >= from && s.CapturedAtUtc < to, ct);
        var recentFlagged = await (
            from s in flaggedQuery
            join d in db.Devices.AsNoTracking() on s.DeviceId equals d.Id
            join e in db.Employees.AsNoTracking() on s.EmployeeId equals e.Id into eg
            from e in eg.DefaultIfEmpty()
            orderby s.CapturedAtUtc descending
            select new FlaggedCaptureDto(s.Id, d.DeviceCode, e == null ? null : e.FullName, s.CapturedAtUtc, s.FlagTerm, s.MatchedOn, s.ClipId))
            .Take(6).ToListAsync(ct);

        return new OverviewDto(
            devices.Count,
            devices.Count(d => d.Status == DeviceHealth.Online),
            devices.Count(d => d.Status == DeviceHealth.Warning),
            devices.Count(d => d.Status is DeviceHealth.Offline or DeviceHealth.ServiceStopped),
            devices.Count(d => d.Status is DeviceHealth.Disabled or DeviceHealth.Revoked),
            live.Count(d => d.Presence == Presence.Active), live.Count(d => d.Presence == Presence.Idle), live.Count(d => d.Presence == Presence.Locked),
            totalEmployees, eventsToday, openAlerts, top, await HourlyAsync(scope, today, offset, ct), recent.Items,
            devices.Where(d => !string.IsNullOrEmpty(d.AgentVersion)).GroupBy(d => d.AgentVersion).Select(g => new VersionCount(g.Key, g.Count())).OrderByDescending(v => v.Count).ToList(),
            flaggedToday, recentFlagged);
    }

    private async Task<IReadOnlyList<HourlyActivity>> HourlyAsync(Scope scope, TimeRange today, int offsetMinutes, CancellationToken ct)
    {
        var rows = await activity.LoadAsync(scope, today, ct);
        var perDevice = rows.Logins.GroupBy(l => l.DeviceId).Select(g =>
            PresenceCalculator.Compute(g, rows.Idles.Where(i => i.DeviceId == g.Key), today, rows.Now)).ToList();

        var result = new List<HourlyActivity>(24);
        for (var h = 0; h < 24; h++)
        {
            // Hour bins are local to the server's offset; the range starts at local midnight.
            var bin = new List<Interval> { new(today.From.AddHours(h), today.From.AddHours(h + 1)) };
            double active = 0, idle = 0;
            foreach (var p in perDevice)
            {
                active += IntervalMath.TotalSeconds(IntervalMath.Intersect(p.Active, bin));
                idle += IntervalMath.TotalSeconds(IntervalMath.Intersect(p.Idle, bin));
            }
            result.Add(new HourlyActivity(h, Math.Round(active, 1), Math.Round(idle, 1)));
        }
        return result;
    }
}
