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
    IAppDbContext db, TimeProvider clock, IActivityLoader activity, IApplicationUsageService usage, ISecurityEventService security) : IOverviewService
{
    public async Task<OverviewDto> GetAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var offset = TimeRange.ServerOffsetMinutes(now, clock);
        var today = TimeRange.ForDay(DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromMinutes(offset)).DateTime), offset);

        var devices = await (from d in db.Devices.AsNoTracking()
                             join s in db.AgentStatuses.AsNoTracking() on d.Id equals s.DeviceId into sg
                             from s in sg.DefaultIfEmpty()
                             select new { d.Status, d.AgentVersion, Presence = s == null ? null : s.Presence }).ToListAsync(ct);
        var live = devices.Where(d => d.Status is DeviceHealth.Online or DeviceHealth.Warning).ToList();

        var from = today.From; var to = today.To;
        var eventsToday = await db.EventIngestions.AsNoTracking().LongCountAsync(e => e.Timestamp >= from && e.Timestamp < to, ct);
        var openAlerts = await db.SecurityEvents.AsNoTracking().CountAsync(e => e.ResolvedAt == null, ct);
        var totalEmployees = await db.Employees.AsNoTracking().CountAsync(ct);

        var top = (await usage.GetTotalsAsync(new UsageFilter(Scope.All), today, ct)).Take(8).ToList();
        var recent = await security.QueryAsync(new SecurityEventFilter(null, null, null, null, null, null), new PageRequest(1, 8), ct);

        return new OverviewDto(
            devices.Count,
            devices.Count(d => d.Status == DeviceHealth.Online),
            devices.Count(d => d.Status == DeviceHealth.Warning),
            devices.Count(d => d.Status is DeviceHealth.Offline or DeviceHealth.ServiceStopped),
            devices.Count(d => d.Status is DeviceHealth.Disabled or DeviceHealth.Revoked),
            live.Count(d => d.Presence == Presence.Active), live.Count(d => d.Presence == Presence.Idle), live.Count(d => d.Presence == Presence.Locked),
            totalEmployees, eventsToday, openAlerts, top, await HourlyAsync(today, offset, ct), recent.Items,
            devices.Where(d => !string.IsNullOrEmpty(d.AgentVersion)).GroupBy(d => d.AgentVersion).Select(g => new VersionCount(g.Key, g.Count())).OrderByDescending(v => v.Count).ToList());
    }

    private async Task<IReadOnlyList<HourlyActivity>> HourlyAsync(TimeRange today, int offsetMinutes, CancellationToken ct)
    {
        var rows = await activity.LoadAsync(Scope.All, today, ct);
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
