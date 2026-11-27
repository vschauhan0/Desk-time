using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Application.Categories;
using NISS.Application.Common;
using NISS.Application.Reporting;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Reports;

internal abstract class ReportGeneratorBase : IReportGenerator
{
    public abstract string Kind { get; }
    public abstract string Title { get; }
    public abstract Task<ReportResult> GenerateAsync(ReportRequest request, CancellationToken ct);

    protected ReportResult Result(ReportRequest r, IReadOnlyList<ReportColumn> columns, IReadOnlyList<Dictionary<string, object?>> rows) =>
        new(Kind, Title, r.Range.From, r.Range.To, columns, rows);

    protected static double S(double seconds) => Math.Round(seconds, 1);
    protected const int RowCap = 10_000;
}

/// <summary>Per-employee day report: login/logout, logged-in, active, idle, locked, top application.</summary>
internal sealed class DailyReport(IAppDbContext db, IActivityLoader activity) : ReportGeneratorBase
{
    public override string Kind => "daily";
    public override string Title => "Daily employee report";

    public override async Task<ReportResult> GenerateAsync(ReportRequest r, CancellationToken ct)
    {
        var rows = await activity.LoadAsync(r.Scope, r.Range, ct);
        var employees = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, ct);
        var from = r.Range.From; var to = r.Range.To;
        var top = await (db.ApplicationSessions.AsNoTracking().InScope(r.Scope)
            .Where(s => s.StartedAt >= from && s.StartedAt < to && s.EmployeeId != null)
            .GroupBy(s => new { s.EmployeeId, s.ProcessName }).Select(g => new { g.Key.EmployeeId, g.Key.ProcessName, Seconds = g.Sum(x => x.DurationSeconds) })).ToListAsync(ct);
        var deviceCodes = await db.Devices.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.DeviceCode, ct);

        var out_ = new List<Dictionary<string, object?>>();
        foreach (var g in rows.Logins.Where(l => l.EmployeeId != null).GroupBy(l => l.EmployeeId!.Value))
        {
            var b = PresenceCalculator.Compute(g, rows.Idles.Where(i => i.EmployeeId == g.Key), r.Range, rows.Now);
            var emp = employees.GetValueOrDefault(g.Key);
            var best = top.Where(t => t.EmployeeId == g.Key).OrderByDescending(t => t.Seconds).FirstOrDefault();
            out_.Add(new()
            {
                ["employeeCode"] = emp?.EmployeeCode, ["employee"] = emp?.FullName, ["department"] = emp?.Department,
                ["firstLogin"] = g.Where(l => l.Login >= from && l.Login < to).Select(l => (DateTimeOffset?)l.Login).Min(),
                ["lastLogout"] = g.Where(l => l.Logout != null && l.Logout >= from && l.Logout < to).Select(l => l.Logout).Max(),
                ["loggedInSeconds"] = S(b.LoggedInSeconds), ["activeSeconds"] = S(b.ActiveSeconds), ["idleSeconds"] = S(b.IdleSeconds), ["lockedSeconds"] = S(b.LockedSeconds),
                ["topApplication"] = best is null ? null : Text.NormalizeProcess(best.ProcessName),
                ["devices"] = string.Join(' ', g.Select(l => deviceCodes.GetValueOrDefault(l.DeviceId, "")).Distinct().OrderBy(x => x)),
            });
        }
        return Result(r,
        [
            Col.Str("employeeCode", "Employee code"), Col.Str("employee", "Employee"), Col.Str("department", "Department"),
            Col.Time("firstLogin", "First login"), Col.Time("lastLogout", "Last logout"), Col.Secs("loggedInSeconds", "Logged in"),
            Col.Secs("activeSeconds", "Active"), Col.Secs("idleSeconds", "Idle"), Col.Secs("lockedSeconds", "Locked"),
            Col.Str("topApplication", "Top application"), Col.Str("devices", "Devices"),
        ], out_.OrderBy(x => (string?)x["employeeCode"]).ToList());
    }
}

/// <summary>Running vs foreground vs interaction time per application.</summary>
internal sealed class ApplicationUsageReport(IApplicationUsageService usage) : ReportGeneratorBase
{
    public override string Kind => "application-usage";
    public override string Title => "Application usage";

    public override async Task<ReportResult> GenerateAsync(ReportRequest r, CancellationToken ct)
    {
        var totals = await usage.GetTotalsAsync(new UsageFilter(r.Scope, r.Application, r.Category), r.Range, ct);
        return Result(r,
        [
            Col.Str("application", "Application"), Col.Str("category", "Category"), Col.Secs("runningSeconds", "Running"),
            Col.Secs("foregroundSeconds", "Foreground"), Col.Secs("interactionSeconds", "Interaction"),
            Col.Num("launches", "Launches"), Col.Num("foregroundTransitions", "Foreground transitions"),
        ], totals.Take(RowCap).Select(t => new Dictionary<string, object?>
        {
            ["application"] = t.ProcessName, ["category"] = t.Category, ["runningSeconds"] = t.RunningSeconds, ["foregroundSeconds"] = t.ForegroundSeconds,
            ["interactionSeconds"] = t.InteractionSeconds, ["launches"] = t.Launches, ["foregroundTransitions"] = t.ForegroundTransitions,
        }).ToList());
    }
}

/// <summary>Active / idle / locked time per employee per local day.</summary>
internal sealed class ActiveIdleReport(IAppDbContext db, IActivityLoader activity) : ReportGeneratorBase
{
    public override string Kind => "active-idle";
    public override string Title => "Active and idle time";

    public override async Task<ReportResult> GenerateAsync(ReportRequest r, CancellationToken ct)
    {
        var rows = await activity.LoadAsync(r.Scope, r.Range, ct);
        var employees = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, ct);
        var offset = TimeSpan.FromMinutes(r.TzOffsetMinutes);
        var firstDay = DateOnly.FromDateTime(r.Range.From.ToOffset(offset).DateTime);
        var lastDay = DateOnly.FromDateTime(r.Range.To.AddTicks(-1).ToOffset(offset).DateTime);
        if (lastDay.DayNumber - firstDay.DayNumber > 92) lastDay = firstDay.AddDays(92);

        var result = new List<Dictionary<string, object?>>();
        foreach (var g in rows.Logins.Where(l => l.EmployeeId != null).GroupBy(l => l.EmployeeId!.Value))
        {
            var emp = employees.GetValueOrDefault(g.Key);
            for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
            {
                var window = TimeRange.ForDay(day, r.TzOffsetMinutes);
                var b = PresenceCalculator.Compute(g, rows.Idles.Where(i => i.EmployeeId == g.Key), window, rows.Now);
                if (b.LoggedInSeconds <= 0) continue;
                result.Add(new()
                {
                    ["date"] = day.ToString("yyyy-MM-dd"), ["employeeCode"] = emp?.EmployeeCode, ["employee"] = emp?.FullName,
                    ["loggedInSeconds"] = S(b.LoggedInSeconds), ["activeSeconds"] = S(b.ActiveSeconds), ["idleSeconds"] = S(b.IdleSeconds), ["lockedSeconds"] = S(b.LockedSeconds),
                });
            }
        }
        return Result(r,
        [
            Col.Str("date", "Date"), Col.Str("employeeCode", "Employee code"), Col.Str("employee", "Employee"),
            Col.Secs("loggedInSeconds", "Logged in"), Col.Secs("activeSeconds", "Active"), Col.Secs("idleSeconds", "Idle"), Col.Secs("lockedSeconds", "Locked"),
        ], result.OrderBy(x => (string?)x["date"]).ThenBy(x => (string?)x["employeeCode"]).Take(RowCap).ToList());
    }
}

/// <summary>Attendance: first login, last logout and worked / idle / locked time per employee per local day.</summary>
internal sealed class AttendanceReport(IAppDbContext db, IActivityLoader activity) : ReportGeneratorBase
{
    public override string Kind => "attendance";
    public override string Title => "Attendance";

    public override async Task<ReportResult> GenerateAsync(ReportRequest r, CancellationToken ct)
    {
        var rows = await activity.LoadAsync(r.Scope, r.Range, ct);
        var employees = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, ct);
        var offset = TimeSpan.FromMinutes(r.TzOffsetMinutes);
        var firstDay = DateOnly.FromDateTime(r.Range.From.ToOffset(offset).DateTime);
        var lastDay = DateOnly.FromDateTime(r.Range.To.AddTicks(-1).ToOffset(offset).DateTime);
        if (lastDay.DayNumber - firstDay.DayNumber > 92) lastDay = firstDay.AddDays(92);

        var result = new List<Dictionary<string, object?>>();
        foreach (var g in rows.Logins.Where(l => l.EmployeeId != null).GroupBy(l => l.EmployeeId!.Value))
        {
            var emp = employees.GetValueOrDefault(g.Key);
            for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
            {
                var window = TimeRange.ForDay(day, r.TzOffsetMinutes);
                var b = PresenceCalculator.Compute(g, rows.Idles.Where(i => i.EmployeeId == g.Key), window, rows.Now);
                if (b.LoggedInSeconds <= 0) continue;
                var from = window.From; var to = window.To;
                result.Add(new()
                {
                    ["date"] = day.ToString("yyyy-MM-dd"),
                    ["employeeCode"] = emp?.EmployeeCode, ["employee"] = emp?.FullName, ["department"] = emp?.Department,
                    ["firstLogin"] = g.Where(l => l.Login >= from && l.Login < to).Select(l => (DateTimeOffset?)l.Login).Min(),
                    ["lastLogout"] = g.Where(l => l.Logout != null && l.Logout >= from && l.Logout < to).Select(l => l.Logout).Max(),
                    ["loggedInSeconds"] = S(b.LoggedInSeconds), ["activeSeconds"] = S(b.ActiveSeconds), ["idleSeconds"] = S(b.IdleSeconds), ["lockedSeconds"] = S(b.LockedSeconds),
                });
            }
        }
        return Result(r,
        [
            Col.Str("date", "Date"), Col.Str("employeeCode", "Employee code"), Col.Str("employee", "Employee"), Col.Str("department", "Department"),
            Col.Time("firstLogin", "First login"), Col.Time("lastLogout", "Last logout"),
            Col.Secs("loggedInSeconds", "Logged in"), Col.Secs("activeSeconds", "Worked"), Col.Secs("idleSeconds", "Idle"), Col.Secs("lockedSeconds", "Locked"),
        ], result.OrderBy(x => (string?)x["date"]).ThenBy(x => (string?)x["employeeCode"]).Take(RowCap).ToList());
    }
}

/// <summary>Heartbeat / resource statistics per device.</summary>
internal sealed class DeviceHealthReport(IAppDbContext db) : ReportGeneratorBase
{
    public override string Kind => "device-health";
    public override string Title => "Device health";

    public override async Task<ReportResult> GenerateAsync(ReportRequest r, CancellationToken ct)
    {
        var from = r.Range.From; var to = r.Range.To;
        var deviceQuery = db.Devices.AsNoTracking().AsQueryable();
        if (r.Scope.Devices is { } ids) deviceQuery = deviceQuery.Where(d => ids.Contains(d.Id));
        if (r.Scope.Employees is { } emps) deviceQuery = deviceQuery.Where(d => d.EmployeeId != null && emps.Contains(d.EmployeeId.Value));
        var devices = await deviceQuery.OrderBy(d => d.DeviceCode).ToListAsync(ct);
        var deviceIds = devices.Select(d => d.Id).ToList();
        var employeeCodes = await db.Employees.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.EmployeeCode, ct);

        var beats = await db.Heartbeats.AsNoTracking().Where(h => deviceIds.Contains(h.DeviceId) && h.Timestamp >= from && h.Timestamp < to)
            .GroupBy(h => h.DeviceId).Select(g => new { g.Key, Count = g.Count(), MaxQueue = g.Max(x => x.QueueDepth) }).ToDictionaryAsync(x => x.Key, ct);
        var metrics = await db.SystemMetrics.AsNoTracking().Where(m => deviceIds.Contains(m.DeviceId) && m.Timestamp >= from && m.Timestamp < to)
            .GroupBy(m => m.DeviceId).Select(g => new { g.Key, AvgCpu = g.Average(x => x.CpuPercent), MaxCpu = g.Max(x => x.CpuPercent), AvgMem = g.Average(x => x.MemoryUsedPercent), MaxDisk = g.Max(x => x.DiskUsedPercent) })
            .ToDictionaryAsync(x => x.Key, ct);
        var outages = await db.SecurityEvents.AsNoTracking().Where(e => e.DeviceId != null && deviceIds.Contains(e.DeviceId.Value) && e.RaisedAt >= from && e.RaisedAt < to && e.Kind == SecurityEventKinds.DeviceOffline)
            .GroupBy(e => e.DeviceId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var rows = devices.Select(d => new Dictionary<string, object?>
        {
            ["device"] = d.DeviceCode, ["hostname"] = d.Hostname, ["employeeCode"] = d.EmployeeId is { } e ? employeeCodes.GetValueOrDefault(e) : null,
            ["status"] = d.Status, ["lastSeen"] = d.LastSeen, ["agentVersion"] = d.AgentVersion,
            ["heartbeats"] = beats.TryGetValue(d.Id, out var b) ? b.Count : 0, ["queueDepth"] = beats.TryGetValue(d.Id, out b) ? b.MaxQueue : 0,
            ["avgCpuPercent"] = metrics.TryGetValue(d.Id, out var m) ? Math.Round(m.AvgCpu, 1) : null, ["maxCpuPercent"] = m is null ? null : Math.Round(m.MaxCpu, 1),
            ["avgMemoryPercent"] = m is null ? null : Math.Round(m.AvgMem, 1), ["diskUsedPercent"] = m?.MaxDisk is { } md ? Math.Round(md, 1) : null,
            ["offlineEvents"] = outages.GetValueOrDefault(d.Id),
        }).ToList();
        return Result(r,
        [
            Col.Str("device", "Device"), Col.Str("hostname", "Hostname"), Col.Str("employeeCode", "Employee"), Col.Str("status", "Status"),
            Col.Time("lastSeen", "Last seen"), Col.Str("agentVersion", "Agent version"), Col.Num("heartbeats", "Heartbeats"), Col.Num("queueDepth", "Queue depth (max)"),
            Col.Num("avgCpuPercent", "Avg CPU %"), Col.Num("maxCpuPercent", "Max CPU %"), Col.Num("avgMemoryPercent", "Avg memory %"),
            Col.Num("diskUsedPercent", "Disk used % (max)"), Col.Num("offlineEvents", "Offline events"),
        ], rows);
    }
}

/// <summary>Process lifetimes.</summary>
internal sealed class ProcessTimelineReport(IApplicationUsageService usage) : ReportGeneratorBase
{
    public override string Kind => "process-timeline";
    public override string Title => "Process timeline";

    public override async Task<ReportResult> GenerateAsync(ReportRequest r, CancellationToken ct)
    {
        var page = await usage.GetProcessesAsync(new ProcessFilter(r.Scope, null, r.Application), r.Range, new PageRequest(1, PageRequest.MaxPageSize), ct);
        var items = new List<ProcessSessionDto>(page.Items);
        for (var p = 2; items.Count < page.Total && items.Count < RowCap; p++)
            items.AddRange((await usage.GetProcessesAsync(new ProcessFilter(r.Scope, null, r.Application), r.Range, new PageRequest(p, PageRequest.MaxPageSize), ct)).Items);
        return Result(r,
        [
            Col.Str("device", "Device"), Col.Str("employeeCode", "Employee"), Col.Str("process", "Process"), Col.Num("pid", "PID"),
            Col.Time("startedAt", "Started"), Col.Time("endedAt", "Ended"), Col.Secs("durationSeconds", "Duration"), Col.Str("user", "User"), Col.Str("path", "Path"),
        ], items.Select(p => new Dictionary<string, object?>
        {
            ["device"] = p.DeviceId, ["employeeCode"] = p.EmployeeCode, ["process"] = p.ProcessName, ["pid"] = p.ProcessId, ["startedAt"] = p.StartedAtUtc,
            ["endedAt"] = p.EndedAtUtc, ["durationSeconds"] = p.DurationSeconds is { } d ? S(d) : null, ["user"] = p.UserName, ["path"] = p.ExecutablePath,
        }).ToList());
    }
}

/// <summary>USB connect/disconnect events.</summary>
internal sealed class UsbReport(IEventQueryService events) : ReportGeneratorBase
{
    public override string Kind => "usb";
    public override string Title => "USB device activity";

    public override async Task<ReportResult> GenerateAsync(ReportRequest r, CancellationToken ct)
    {
        var page = await events.UsbAsync(new DeviceEventFilter(r.Scope, r.Range), new PageRequest(1, PageRequest.MaxPageSize), ct);
        var items = new List<UsbEventDto>(page.Items);
        for (var p = 2; items.Count < page.Total && items.Count < RowCap; p++)
            items.AddRange((await events.UsbAsync(new DeviceEventFilter(r.Scope, r.Range), new PageRequest(p, PageRequest.MaxPageSize), ct)).Items);
        return Result(r,
        [
            Col.Time("timestamp", "Time"), Col.Str("device", "Device"), Col.Str("employeeCode", "Employee"), Col.Str("event", "Event"),
            Col.Str("deviceType", "Type"), Col.Str("vendor", "Vendor"), Col.Str("product", "Product"), Col.Str("serial", "Serial number"),
        ], items.Select(u => new Dictionary<string, object?>
        {
            ["timestamp"] = u.TimestampUtc, ["device"] = u.DeviceId, ["employeeCode"] = u.EmployeeCode, ["event"] = u.EventType, ["deviceType"] = u.DeviceType,
            ["vendor"] = u.Vendor, ["product"] = u.Product, ["serial"] = u.SerialNumber,
        }).ToList());
    }
}

/// <summary>Agent status per device (queue, errors, versions, clock).</summary>
internal sealed class AgentHealthReport(IAppDbContext db) : ReportGeneratorBase
{
    public override string Kind => "agent-health";
    public override string Title => "Agent health";

    public override async Task<ReportResult> GenerateAsync(ReportRequest r, CancellationToken ct)
    {
        var q = from d in db.Devices.AsNoTracking()
                join s in db.AgentStatuses.AsNoTracking() on d.Id equals s.DeviceId into sg
                from s in sg.DefaultIfEmpty()
                select new { d, s };
        if (r.Scope.Devices is { } ids) q = q.Where(x => ids.Contains(x.d.Id));
        if (r.Scope.Employees is { } emps) q = q.Where(x => x.d.EmployeeId != null && emps.Contains(x.d.EmployeeId.Value));
        var list = await q.OrderBy(x => x.d.DeviceCode).ToListAsync(ct);
        var open = await db.SecurityEvents.AsNoTracking().Where(e => e.ResolvedAt == null && e.DeviceId != null)
            .GroupBy(e => e.DeviceId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return Result(r,
        [
            Col.Str("device", "Device"), Col.Str("hostname", "Hostname"), Col.Str("status", "Status"), Col.Str("agentVersion", "Agent version"),
            Col.Str("serviceState", "Service state"), Col.Time("lastHeartbeat", "Last heartbeat"), Col.Num("queueDepth", "Queue depth"),
            Col.Num("deadLetters", "Dead letters"), Col.Time("lastUpload", "Last upload"), Col.Str("lastError", "Last error"),
            Col.Num("clockOffsetMs", "Clock offset (ms)"), Col.Num("openIssues", "Open issues"),
        ], list.Select(x => new Dictionary<string, object?>
        {
            ["device"] = x.d.DeviceCode, ["hostname"] = x.d.Hostname, ["status"] = x.d.Status, ["agentVersion"] = x.d.AgentVersion,
            ["serviceState"] = x.s?.ServiceState, ["lastHeartbeat"] = x.s?.LastHeartbeatAt ?? x.d.LastSeen, ["queueDepth"] = x.s?.QueueDepth ?? 0,
            ["deadLetters"] = x.s?.DeadLetterCount ?? 0, ["lastUpload"] = x.s?.LastUploadAt, ["lastError"] = x.s?.LastError ?? x.s?.LastUploadError,
            ["clockOffsetMs"] = x.s?.ClockOffsetMs ?? 0, ["openIssues"] = open.GetValueOrDefault(x.d.Id),
        }).ToList());
    }
}

/// <summary>Per-department totals.</summary>
internal sealed class DepartmentSummaryReport(IAppDbContext db, IActivityLoader activity) : ReportGeneratorBase
{
    public override string Kind => "department-summary";
    public override string Title => "Department summary";

    public override async Task<ReportResult> GenerateAsync(ReportRequest r, CancellationToken ct)
    {
        var employees = await db.Employees.AsNoTracking().ToListAsync(ct);
        var devicesByEmployee = await db.Devices.AsNoTracking().Where(d => d.EmployeeId != null).GroupBy(d => d.EmployeeId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var rows = await activity.LoadAsync(r.Scope, r.Range, ct);

        var result = new List<Dictionary<string, object?>>();
        foreach (var dept in employees.GroupBy(e => string.IsNullOrWhiteSpace(e.Department) ? "(none)" : e.Department!).OrderBy(g => g.Key))
        {
            var ids = dept.Select(e => e.Id).ToHashSet();
            var logins = rows.Logins.Where(l => l.EmployeeId is { } e && ids.Contains(e)).ToList();
            double loggedIn = 0, active = 0, idle = 0, locked = 0;
            foreach (var g in logins.GroupBy(l => l.EmployeeId!.Value))
            {
                var b = PresenceCalculator.Compute(g, rows.Idles.Where(i => i.EmployeeId == g.Key), r.Range, rows.Now);
                loggedIn += b.LoggedInSeconds; active += b.ActiveSeconds; idle += b.IdleSeconds; locked += b.LockedSeconds;
            }
            result.Add(new()
            {
                ["department"] = dept.Key, ["employees"] = dept.Count(), ["devices"] = dept.Sum(e => devicesByEmployee.GetValueOrDefault(e.Id)),
                ["activeEmployees"] = logins.Select(l => l.EmployeeId).Distinct().Count(),
                ["loggedInSeconds"] = S(loggedIn), ["activeSeconds"] = S(active), ["idleSeconds"] = S(idle), ["lockedSeconds"] = S(locked),
            });
        }
        return Result(r,
        [
            Col.Str("department", "Department"), Col.Num("employees", "Employees"), Col.Num("devices", "Devices"), Col.Num("activeEmployees", "Employees seen"),
            Col.Secs("loggedInSeconds", "Logged in"), Col.Secs("activeSeconds", "Active"), Col.Secs("idleSeconds", "Idle"), Col.Secs("lockedSeconds", "Locked"),
        ], result);
    }
}
