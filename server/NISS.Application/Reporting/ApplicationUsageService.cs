using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Application.Categories;
using NISS.Application.Common;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Reporting;

/// <summary>Filters for application usage queries (device/employee/department are expressed by <see cref="Scope"/>).</summary>
public sealed record UsageFilter(Scope Scope, string? Application = null, string? Category = null);

/// <summary>Filters for process listings.</summary>
public sealed record ProcessFilter(Scope Scope, string? Status = null, string? Search = null);

/// <summary>Application and process usage: running vs foreground vs interaction time, launches and transitions.</summary>
public interface IApplicationUsageService
{
    Task<IReadOnlyList<AppUsageTotal>> GetTotalsAsync(UsageFilter filter, TimeRange range, CancellationToken ct);
    Task<ApplicationActivityResponse> GetActivityAsync(UsageFilter filter, TimeRange range, PageRequest page, CancellationToken ct);
    Task<Paged<ProcessSessionDto>> GetProcessesAsync(ProcessFilter filter, TimeRange range, PageRequest page, CancellationToken ct);
}

internal sealed class ApplicationUsageService(IAppDbContext db, ICategoryService categories, TimeProvider clock) : IApplicationUsageService
{
    private sealed class Acc
    {
        public string Name = "";
        public double Running, Foreground, Interaction;
        public int Launches, Transitions;
    }

    public async Task<IReadOnlyList<AppUsageTotal>> GetTotalsAsync(UsageFilter f, TimeRange range, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var matcher = await categories.GetMatcherAsync(ct);
        var from = range.From; var to = range.To;
        var accs = new Dictionary<string, Acc>(StringComparer.OrdinalIgnoreCase);
        Acc Get(string name)
        {
            var key = Text.NormalizeProcess(name);
            if (!accs.TryGetValue(key, out var a)) accs[key] = a = new Acc { Name = key };
            return a;
        }
        var like = string.IsNullOrWhiteSpace(f.Application) ? null : QueryExtensions.LikeContains(f.Application.Trim());

        // Foreground / interaction from application_sessions (closed rows use the agent's monotonic durations, clipped proportionally).
        var sessionQuery = db.ApplicationSessions.AsNoTracking().InScope(f.Scope).Where(s => s.StartedAt < to && (s.EndedAt == null || s.EndedAt > from));
        if (like is not null) sessionQuery = sessionQuery.Where(s => EF.Functions.Like(s.ProcessName.ToLower(), like));
        var sessions = await (from s in sessionQuery
                              join d in db.Devices.AsNoTracking() on s.DeviceId equals d.Id
                              select new { s.ProcessName, s.StartedAt, s.EndedAt, s.DurationSeconds, s.InteractionSeconds, d.LastSeen }).ToListAsync(ct);
        foreach (var s in sessions)
        {
            var a = Get(s.ProcessName);
            var end = s.EndedAt ?? OpenEnd(s.StartedAt, s.LastSeen, now);
            var wall = (end - s.StartedAt).TotalSeconds;
            var clipped = (Min(end, to) - Max(s.StartedAt, from)).TotalSeconds;
            if (clipped <= 0) continue;
            var fraction = wall <= 0 ? 1 : Math.Min(1, clipped / wall);
            var duration = s.EndedAt is null ? wall : s.DurationSeconds;
            a.Foreground += duration * fraction;
            a.Interaction += s.InteractionSeconds * fraction;
            if (s.StartedAt >= from && s.StartedAt < to) a.Transitions++;
        }

        // Running time from process lifetimes.
        var processQuery = db.ProcessSessions.AsNoTracking().InScope(f.Scope).Where(p => p.StartedAt < to && (p.EndedAt == null || p.EndedAt > from));
        if (like is not null) processQuery = processQuery.Where(p => EF.Functions.Like(p.ProcessName.ToLower(), like));
        var processes = await (from p in processQuery
                               join d in db.Devices.AsNoTracking() on p.DeviceId equals d.Id
                               select new { p.ProcessName, p.StartedAt, p.EndedAt, d.LastSeen }).ToListAsync(ct);
        var running = new Dictionary<string, List<Interval>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in processes)
        {
            var key = Text.NormalizeProcess(p.ProcessName);
            var end = p.EndedAt ?? OpenEnd(p.StartedAt, p.LastSeen, now);
            var iv = new Interval(Max(p.StartedAt, from), Min(end, to));
            if (iv.IsEmpty) continue;
            if (!running.TryGetValue(key, out var list)) running[key] = list = [];
            list.Add(iv);
            Get(p.ProcessName);
        }
        foreach (var (key, list) in running) accs[key].Running = IntervalMath.TotalSeconds(IntervalMath.Normalize(list));

        // Launches = APPLICATION_STARTED events.
        var launchQuery = db.EventIngestions.AsNoTracking().InScope(f.Scope)
            .Where(e => e.EventType == EventTypes.ApplicationStarted && e.Timestamp >= from && e.Timestamp < to && e.Subject != null);
        if (like is not null) launchQuery = launchQuery.Where(e => EF.Functions.Like(e.Subject!.ToLower(), like));
        var launches = await launchQuery.GroupBy(e => e.Subject!).Select(g => new { Name = g.Key, Count = g.Count() }).ToListAsync(ct);
        foreach (var l in launches) Get(l.Name).Launches += l.Count;

        var result = new List<AppUsageTotal>();
        foreach (var a in accs.Values)
        {
            var category = matcher.Categorize(a.Name);
            if (f.Category is { Length: > 0 } wanted && !string.Equals(category, wanted, StringComparison.OrdinalIgnoreCase)) continue;
            // Background system processes that were never used add noise, not information.
            if (a.Foreground <= 0 && a.Launches == 0 && category == AppCategories.System) continue;
            var runningSeconds = Math.Max(a.Running, a.Foreground);
            if (runningSeconds <= 0 && a.Launches == 0 && a.Transitions == 0) continue;
            result.Add(new AppUsageTotal(a.Name, category, Round(runningSeconds), Round(a.Foreground), Round(a.Interaction), a.Launches, a.Transitions));
        }
        return result.OrderByDescending(r => r.ForegroundSeconds).ThenByDescending(r => r.RunningSeconds).ThenBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<ApplicationActivityResponse> GetActivityAsync(UsageFilter f, TimeRange range, PageRequest page, CancellationToken ct)
    {
        var totals = await GetTotalsAsync(f, range, ct);
        var matcher = await categories.GetMatcherAsync(ct);
        var from = range.From; var to = range.To;

        var q = db.ApplicationSessions.AsNoTracking().InScope(f.Scope).Where(s => s.StartedAt < to && (s.EndedAt == null || s.EndedAt > from));
        if (!string.IsNullOrWhiteSpace(f.Application)) { var like = QueryExtensions.LikeContains(f.Application.Trim()); q = q.Where(s => EF.Functions.Like(s.ProcessName.ToLower(), like)); }
        if (!string.IsNullOrWhiteSpace(f.Category))
        {
            var names = await q.Select(s => s.ProcessName).Distinct().ToListAsync(ct);
            var inCategory = names.Where(n => string.Equals(matcher.Categorize(n), f.Category, StringComparison.OrdinalIgnoreCase)).ToList();
            q = q.Where(s => inCategory.Contains(s.ProcessName));
        }

        var paged = await q.OrderByDescending(s => s.StartedAt).ToPagedAsync(page, async rows =>
        {
            var deviceIds = rows.Select(r => r.DeviceId).Distinct().ToList();
            var devices = await db.Devices.AsNoTracking().Where(d => deviceIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.DeviceCode, ct);
            var empIds = rows.Where(r => r.EmployeeId != null).Select(r => r.EmployeeId!.Value).Distinct().ToList();
            var emps = await db.Employees.AsNoTracking().Where(e => empIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.EmployeeCode, ct);
            var now = clock.GetUtcNow();
            return (IReadOnlyList<ApplicationSessionDto>)rows.Select(r => new ApplicationSessionDto(
                r.Id, devices.GetValueOrDefault(r.DeviceId, ""), r.EmployeeId is { } e ? emps.GetValueOrDefault(e) : null,
                r.ProcessName, r.ExecutablePath, r.WindowTitle, matcher.Categorize(r.ProcessName), r.StartedAt, r.EndedAt,
                r.EndedAt is null ? Math.Max(0, (now - r.StartedAt).TotalSeconds) : r.DurationSeconds, r.InteractionSeconds, r.IdleSeconds, r.SessionType)).ToList();
        }, ct);
        return new ApplicationActivityResponse(totals, paged);
    }

    public async Task<Paged<ProcessSessionDto>> GetProcessesAsync(ProcessFilter f, TimeRange range, PageRequest page, CancellationToken ct)
    {
        var from = range.From; var to = range.To;
        var q = db.ProcessSessions.AsNoTracking().InScope(f.Scope).Where(p => p.StartedAt < to && (p.EndedAt == null || p.EndedAt > from));
        var status = f.Status?.Trim().ToUpperInvariant();
        if (status == "RUNNING") q = q.Where(p => p.EndedAt == null);
        else if (status == "STOPPED") q = q.Where(p => p.EndedAt != null);
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var like = QueryExtensions.LikeContains(f.Search.Trim());
            q = q.Where(p => EF.Functions.Like(p.ProcessName.ToLower(), like) || (p.ExecutablePath != null && EF.Functions.Like(p.ExecutablePath.ToLower(), like)));
        }

        return await q.OrderByDescending(p => p.StartedAt).ToPagedAsync(page, async rows =>
        {
            var deviceIds = rows.Select(r => r.DeviceId).Distinct().ToList();
            var devices = await db.Devices.AsNoTracking().Where(d => deviceIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.DeviceCode, ct);
            var empIds = rows.Where(r => r.EmployeeId != null).Select(r => r.EmployeeId!.Value).Distinct().ToList();
            var emps = await db.Employees.AsNoTracking().Where(e => empIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.EmployeeCode, ct);
            return (IReadOnlyList<ProcessSessionDto>)rows.Select(r => new ProcessSessionDto(
                r.Id, devices.GetValueOrDefault(r.DeviceId, ""), r.EmployeeId is { } e ? emps.GetValueOrDefault(e) : null, r.ProcessId, r.ParentProcessId,
                r.ProcessName, r.ExecutablePath, r.UserName, r.StartedAt, r.EndedAt,
                r.EndedAt is { } end ? Math.Max(0, (end - r.StartedAt).TotalSeconds) : null,
                r.EndedAt is null ? "RUNNING" : "STOPPED", r.CommandLine)).ToList();
        }, ct);
    }

    private static DateTimeOffset OpenEnd(DateTimeOffset start, DateTimeOffset? lastSeen, DateTimeOffset now) =>
        lastSeen is { } seen && seen > start && seen < now ? seen : now;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    private static double Round(double v) => Math.Round(v, 1);
}
