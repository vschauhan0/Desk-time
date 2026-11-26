using Microsoft.EntityFrameworkCore;
using NISS.Application.Abstractions;
using NISS.Application.Common;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Audit;

/// <summary>Well-known audit action names.</summary>
public static class AuditActions
{
    public const string Login = "LOGIN";
    public const string LoginFailed = "LOGIN_FAILED";
    public const string Logout = "LOGOUT";
    public const string DeviceEnrolled = "DEVICE_ENROLLED";
    public const string DeviceDisabled = "DEVICE_DISABLED";
    public const string DeviceEnabled = "DEVICE_ENABLED";
    public const string DeviceRevoked = "DEVICE_REVOKED";
    public const string DeviceReEnrollRequested = "DEVICE_REENROLL_REQUESTED";
    public const string DeviceSecretRotated = "DEVICE_SECRET_ROTATED";
    public const string EnrollmentTokenCreated = "ENROLLMENT_TOKEN_CREATED";
    public const string EnrollmentTokenDeleted = "ENROLLMENT_TOKEN_DELETED";
    public const string EmployeeCreated = "EMPLOYEE_CREATED";
    public const string EmployeeUpdated = "EMPLOYEE_UPDATED";
    public const string SettingsUpdated = "SETTINGS_UPDATED";
    public const string DiagnosticsDownloaded = "DIAGNOSTICS_DOWNLOADED";
    public const string ReportExported = "REPORT_EXPORTED";
    public const string AdminUserCreated = "ADMIN_USER_CREATED";
    public const string AdminUserUpdated = "ADMIN_USER_UPDATED";
    public const string AdminUserDeleted = "ADMIN_USER_DELETED";
    public const string SecurityEventResolved = "SECURITY_EVENT_RESOLVED";
    public const string ScreenshotViewed = "SCREENSHOT_VIEWED";
    public const string LiveViewStarted = "LIVE_VIEW_STARTED";
    public const string LiveViewEnded = "LIVE_VIEW_ENDED";
}

internal sealed class AuditService(IAppDbContext db, ICurrentUser user, TimeProvider clock) : IAuditService
{
    public void Record(string action, string? target = null, string? detail = null) =>
        RecordAs(user.UserId, user.Username, action, target, detail);

    public void RecordAs(Guid? userId, string? username, string action, string? target = null, string? detail = null) =>
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            Timestamp = clock.GetUtcNow(),
            AdminUserId = userId,
            Username = Text.Trunc(username, 256),
            Action = action,
            Target = Text.Trunc(target, 256),
            Detail = Text.Trunc(detail, 2000),
            IpAddress = user.IpAddress,
        });

    public async Task WriteAsync(string action, string? target = null, string? detail = null, CancellationToken ct = default)
    {
        Record(action, target, detail);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Reads the audit log.</summary>
public interface IAuditQueryService
{
    Task<Paged<AuditLogDto>> QueryAsync(DateTimeOffset? from, DateTimeOffset? to, string? user, string? action, PageRequest page, CancellationToken ct);
}

internal sealed class AuditQueryService(IAppDbContext db) : IAuditQueryService
{
    public Task<Paged<AuditLogDto>> QueryAsync(DateTimeOffset? from, DateTimeOffset? to, string? user, string? action, PageRequest page, CancellationToken ct)
    {
        var q = db.AuditLogs.AsNoTracking().AsQueryable();
        if (from is { } f) q = q.Where(a => a.Timestamp >= f.ToUniversalTime());
        if (to is { } t) q = q.Where(a => a.Timestamp < t.ToUniversalTime());
        if (!string.IsNullOrWhiteSpace(user)) q = q.Where(a => a.Username != null && EF.Functions.Like(a.Username.ToLower(), QueryExtensions.LikeContains(user.Trim())));
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action == action.Trim().ToUpper());
        return q.OrderByDescending(a => a.Timestamp)
            .ToPagedAsync(page, a => new AuditLogDto(a.Id, a.Timestamp, a.Username, a.Action, a.Target, a.Detail, a.IpAddress), ct);
    }
}
