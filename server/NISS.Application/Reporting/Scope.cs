using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Domain.Entities;

namespace NISS.Application.Reporting;

/// <summary>Restricts a query to some devices and/or employees. <c>null</c> = unrestricted, an empty list = matches nothing.</summary>
public sealed record Scope(IReadOnlyCollection<Guid>? Devices = null, IReadOnlyCollection<Guid>? Employees = null)
{
    public static Scope All { get; } = new();
    public static Scope ForDevice(Guid id) => new([id]);
    public static Scope ForEmployee(Guid id) => new(null, [id]);
}

/// <summary>Query helpers for <see cref="Scope"/>.</summary>
public static class ScopeExtensions
{
    public static IQueryable<T> InScope<T>(this IQueryable<T> query, Scope scope) where T : IDeviceOwned
    {
        if (scope.Devices is { } devices) query = query.Where(x => devices.Contains(x.DeviceId));
        if (scope.Employees is { } employees) query = query.Where(x => x.EmployeeId != null && employees.Contains(x.EmployeeId.Value));
        return query;
    }
}

/// <summary>Turns the device/employee/department query parameters into a <see cref="Scope"/>.</summary>
public interface IScopeResolver
{
    Task<Scope> ResolveAsync(string? deviceCode, string? employeeCode, string? department, CancellationToken ct);
}

internal sealed class ScopeResolver(IAppDbContext db) : IScopeResolver
{
    public async Task<Scope> ResolveAsync(string? deviceCode, string? employeeCode, string? department, CancellationToken ct)
    {
        List<Guid>? devices = null, employees = null;
        if (!string.IsNullOrWhiteSpace(deviceCode))
        {
            var code = deviceCode.Trim();
            devices = await db.Devices.AsNoTracking().Where(d => d.DeviceCode == code).Select(d => d.Id).ToListAsync(ct);
        }
        if (!string.IsNullOrWhiteSpace(employeeCode))
        {
            var code = employeeCode.Trim().ToLower();
            employees = await db.Employees.AsNoTracking().Where(e => e.EmployeeCode.ToLower() == code).Select(e => e.Id).ToListAsync(ct);
        }
        if (!string.IsNullOrWhiteSpace(department))
        {
            var dept = department.Trim().ToLower();
            var inDept = await db.Employees.AsNoTracking().Where(e => e.Department != null && e.Department.ToLower() == dept).Select(e => e.Id).ToListAsync(ct);
            employees = employees is null ? inDept : employees.Intersect(inDept).ToList();
        }
        return new Scope(devices, employees);
    }
}
