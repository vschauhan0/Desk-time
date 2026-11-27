using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Monitor.Protocol;

namespace NISS.Application.Auth;

/// <summary>
/// The data the current dashboard user is allowed to see. <see cref="All"/> is true for a SUPERADMIN or any user not
/// restricted to specific employees; otherwise <see cref="EmployeeIds"/> and <see cref="DeviceIds"/> are the exact
/// sets they may see (fail-closed: an empty set means "nothing").
/// </summary>
/// <param name="All">True when the user sees every employee/device (employee scope only; page access is separate).</param>
/// <param name="PageLevels">Page/sub-page access map; null = EDIT on every page (unrestricted page access).</param>
public sealed record AccessScope(
    bool All,
    IReadOnlySet<Guid> EmployeeIds,
    IReadOnlySet<Guid> DeviceIds,
    IReadOnlyDictionary<string, string>? PageLevels = null)
{
    public static AccessScope Unrestricted { get; } = new(true, new HashSet<Guid>(), new HashSet<Guid>(), null);

    /// <summary>Whether a given employee is visible to this user.</summary>
    public bool CanSeeEmployee(Guid employeeId) => All || EmployeeIds.Contains(employeeId);

    /// <summary>Whether a given device is visible to this user.</summary>
    public bool CanSeeDevice(Guid deviceId) => All || DeviceIds.Contains(deviceId);

    /// <summary>The access level for a page/sub-page; sub-pages inherit their parent when not set explicitly.</summary>
    public string Level(string pageKey) => RbacMapping.LevelOf(PageLevels, pageKey);

    public bool CanViewPage(string pageKey) => PageAccess.CanView(Level(pageKey));
    public bool CanEditPage(string pageKey) => PageAccess.CanEdit(Level(pageKey));
}

/// <summary>Resolves the current user's <see cref="AccessScope"/> (cached for the request).</summary>
public interface IAccessScopeProvider
{
    Task<AccessScope> CurrentAsync(CancellationToken ct);

    /// <summary>Throws <see cref="ForbiddenException"/> unless the user has EDIT on the page (SUPERADMIN always passes).</summary>
    Task RequireEditAsync(string pageKey, CancellationToken ct);

    /// <summary>Throws <see cref="ForbiddenException"/> unless the user can at least VIEW the page.</summary>
    Task RequireViewAsync(string pageKey, CancellationToken ct);
}

internal sealed class AccessScopeProvider(IAppDbContext db, ICurrentUser current) : IAccessScopeProvider
{
    private AccessScope? _cached;

    public async Task RequireEditAsync(string pageKey, CancellationToken ct)
    {
        if (!(await CurrentAsync(ct)).CanEditPage(pageKey))
            throw new ForbiddenException("You do not have edit access to this area.");
    }

    public async Task RequireViewAsync(string pageKey, CancellationToken ct)
    {
        if (!(await CurrentAsync(ct)).CanViewPage(pageKey))
            throw new ForbiddenException("You do not have access to this area.");
    }

    public async Task<AccessScope> CurrentAsync(CancellationToken ct)
    {
        if (_cached is not null) return _cached;

        // No identity (e.g. a background/worker context) or a SUPERADMIN: unrestricted.
        if (current.UserId is not { } userId || current.Role == Roles.SuperAdmin)
            return _cached = AccessScope.Unrestricted;

        var user = await db.AdminUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || user.Role == Roles.SuperAdmin) return _cached = AccessScope.Unrestricted;

        // Page access and employee scope are independent: a user may be page-restricted but see all employees, or vice versa.
        var pageLevels = RbacMapping.EffectivePageAccess(user); // null = EDIT everywhere

        if (user.ScopeAllEmployees)
            return _cached = new AccessScope(true, new HashSet<Guid>(), new HashSet<Guid>(), pageLevels);

        // Explicit employees plus everyone in the allowed departments.
        var employeeIds = new HashSet<Guid>(RbacMapping.ReadGuids(user.AllowedEmployeeIdsJson) ?? []);
        var departments = RbacMapping.ReadStrings(user.AllowedDepartmentsJson) ?? [];
        if (departments.Count > 0)
        {
            var lowered = departments.Select(d => d.ToLowerInvariant()).ToList();
            var deptEmployees = await db.Employees.AsNoTracking()
                .Where(e => e.Department != null && lowered.Contains(e.Department.ToLower()))
                .Select(e => e.Id).ToListAsync(ct);
            foreach (var id in deptEmployees) employeeIds.Add(id);
        }

        // Device-owned data is scoped by device: a device is visible when its employee is visible.
        var deviceIds = employeeIds.Count == 0
            ? new HashSet<Guid>()
            : new HashSet<Guid>(await db.Devices.AsNoTracking()
                .Where(d => d.EmployeeId != null && employeeIds.Contains(d.EmployeeId.Value))
                .Select(d => d.Id).ToListAsync(ct));

        return _cached = new AccessScope(false, employeeIds, deviceIds, pageLevels);
    }
}
