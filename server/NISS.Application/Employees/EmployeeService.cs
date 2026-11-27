using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Application.Audit;
using NISS.Application.Common;
using NISS.Application.Reporting;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Employees;

/// <summary>Employee administration and per-employee summaries.</summary>
public interface IEmployeeService
{
    Task<Paged<EmployeeDto>> ListAsync(string? search, string? department, PageRequest page, CancellationToken ct);
    Task<EmployeeDto> GetAsync(Guid id, CancellationToken ct);
    Task<EmployeeDto> CreateAsync(EmployeeUpsert request, CancellationToken ct);
    Task<EmployeeDto> UpdateAsync(Guid id, EmployeeUpsert request, CancellationToken ct);
    Task<IReadOnlyList<string>> DepartmentsAsync(CancellationToken ct);
    Task<EmployeeDaySummary> DaySummaryAsync(Guid id, DateOnly? date, int? tzOffsetMinutes, CancellationToken ct);
}

internal sealed class EmployeeService(
    IAppDbContext db, TimeProvider clock, IAuditService audit, IActivityLoader activity, IApplicationUsageService usage,
    Auth.IAccessScopeProvider access) : IEmployeeService
{
    public async Task<Paged<EmployeeDto>> ListAsync(string? search, string? department, PageRequest page, CancellationToken ct)
    {
        var q = db.Employees.AsNoTracking().AsQueryable();
        // RBAC: a restricted user only sees the employees they are scoped to.
        var scope = await access.CurrentAsync(ct);
        if (!scope.All)
        {
            var allowed = scope.EmployeeIds.ToList();
            q = q.Where(e => allowed.Contains(e.Id));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = QueryExtensions.LikeContains(search.Trim());
            q = q.Where(e => EF.Functions.Like(e.FullName.ToLower(), like) || EF.Functions.Like(e.EmployeeCode.ToLower(), like) || (e.Email != null && EF.Functions.Like(e.Email.ToLower(), like)));
        }
        if (!string.IsNullOrWhiteSpace(department)) { var dept = department.Trim().ToLower(); q = q.Where(e => e.Department != null && e.Department.ToLower() == dept); }
        return await q.OrderBy(e => e.EmployeeCode).ToPagedAsync(page, rows => MapAsync(rows, ct), ct);
    }

    public async Task<EmployeeDto> GetAsync(Guid id, CancellationToken ct)
    {
        // A restricted user gets 404 (not 403) for an employee outside their scope, so existence is not revealed.
        if (!(await access.CurrentAsync(ct)).CanSeeEmployee(id)) throw new NotFoundException("Employee not found.");
        var e = await db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Employee not found.");
        return (await MapAsync([e], ct))[0];
    }

    public async Task<EmployeeDto> CreateAsync(EmployeeUpsert r, CancellationToken ct)
    {
        var code = r.EmployeeCode.Trim();
        if (await db.Employees.AnyAsync(e => e.EmployeeCode.ToLower() == code.ToLower(), ct)) throw new ConflictException($"Employee code {code} already exists.");
        var now = clock.GetUtcNow();
        var e = new Employee
        {
            Id = Guid.NewGuid(), EmployeeCode = code, FullName = r.FullName.Trim(), Department = Clean(r.Department), Email = Clean(r.Email),
            Active = r.Active, CreatedAt = now, UpdatedAt = now,
        };
        db.Employees.Add(e);
        audit.Record(AuditActions.EmployeeCreated, code, e.FullName);
        await db.SaveChangesAsync(ct);
        return (await MapAsync([e], ct))[0];
    }

    public async Task<EmployeeDto> UpdateAsync(Guid id, EmployeeUpsert r, CancellationToken ct)
    {
        var e = await db.Employees.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Employee not found.");
        var code = r.EmployeeCode.Trim();
        if (await db.Employees.AnyAsync(x => x.Id != id && x.EmployeeCode.ToLower() == code.ToLower(), ct)) throw new ConflictException($"Employee code {code} already exists.");
        e.EmployeeCode = code; e.FullName = r.FullName.Trim(); e.Department = Clean(r.Department); e.Email = Clean(r.Email);
        e.Active = r.Active; e.UpdatedAt = clock.GetUtcNow();
        audit.Record(AuditActions.EmployeeUpdated, code, e.FullName);
        await db.SaveChangesAsync(ct);
        return (await MapAsync([e], ct))[0];
    }

    public async Task<IReadOnlyList<string>> DepartmentsAsync(CancellationToken ct) =>
        await db.Employees.AsNoTracking().Where(e => e.Department != null && e.Department != "").Select(e => e.Department!).Distinct().OrderBy(d => d).ToListAsync(ct);

    public async Task<EmployeeDaySummary> DaySummaryAsync(Guid id, DateOnly? date, int? tzOffsetMinutes, CancellationToken ct)
    {
        var employee = await GetAsync(id, ct);
        var now = clock.GetUtcNow();
        var offset = tzOffsetMinutes ?? TimeRange.ServerOffsetMinutes(now, clock);
        var day = date ?? DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromMinutes(offset)).DateTime);
        var range = TimeRange.ForDay(day, offset);
        var scope = Scope.ForEmployee(id);

        var rows = await activity.LoadAsync(scope, range, ct);
        var breakdown = PresenceCalculator.Compute(rows.Logins, rows.Idles, range, rows.Now);
        var apps = await usage.GetTotalsAsync(new UsageFilter(scope), range, ct);

        var inDay = rows.Logins.Where(l => l.Login >= range.From && l.Login < range.To).ToList();
        var logouts = rows.Logins.Where(l => l.Logout is { } o && o >= range.From && o < range.To).Select(l => l.Logout!.Value).ToList();
        var deviceIds = rows.Logins.Select(l => l.DeviceId).Distinct().ToList();
        var codes = await db.Devices.AsNoTracking().Where(d => deviceIds.Contains(d.Id)).Select(d => d.DeviceCode).OrderBy(c => c).ToListAsync(ct);

        return new EmployeeDaySummary(
            employee, day.ToString("yyyy-MM-dd"),
            inDay.Count > 0 ? inDay.Min(l => l.Login) : null,
            logouts.Count > 0 ? logouts.Max() : null,
            rows.Logins.Any(l => l.Logout is null && l.Login <= now),
            Math.Round(breakdown.LoggedInSeconds, 1), Math.Round(breakdown.ActiveSeconds, 1),
            Math.Round(breakdown.IdleSeconds, 1), Math.Round(breakdown.LockedSeconds, 1), apps, codes);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async Task<IReadOnlyList<EmployeeDto>> MapAsync(IReadOnlyList<Employee> employees, CancellationToken ct)
    {
        var ids = employees.Select(e => e.Id).ToList();
        var devices = await (from d in db.Devices.AsNoTracking()
                             join s in db.AgentStatuses.AsNoTracking() on d.Id equals s.DeviceId into sg
                             from s in sg.DefaultIfEmpty()
                             where d.EmployeeId != null && ids.Contains(d.EmployeeId.Value)
                             select new { d.EmployeeId, d.Status, d.LastSeen, Presence = s == null ? null : s.Presence }).ToListAsync(ct);
        return employees.Select(e =>
        {
            var mine = devices.Where(d => d.EmployeeId == e.Id).OrderByDescending(d => d.LastSeen).ToList();
            var latest = mine.FirstOrDefault();
            var presence = latest is not null && latest.Status is DeviceHealth.Online or DeviceHealth.Warning ? latest.Presence : (latest is null ? null : Presence.Unknown);
            return new EmployeeDto(e.Id, e.EmployeeCode, e.FullName, e.Department, e.Email, e.Active, mine.Count, latest?.Status, presence, latest?.LastSeen);
        }).ToList();
    }
}
