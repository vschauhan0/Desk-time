using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NISS.Application.Abstractions;
using NISS.Application.Audit;
using NISS.Application.Common;
using NISS.Application.Options;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Auth;

/// <summary>Dashboard login, token refresh and logout.</summary>
public interface IAuthService
{
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct);
    Task<AuthResponse> RefreshAsync(RefreshRequest request, CancellationToken ct);
    Task LogoutAsync(string? refreshToken, CancellationToken ct);
    Task<UserInfo> MeAsync(Guid userId, CancellationToken ct);
}

internal sealed class AuthService(
    IAppDbContext db, IPasswordService passwords, ISecretHasher hasher, IAccessTokenIssuer issuer, IAuditService audit,
    TimeProvider clock, IOptions<SecurityOptions> options, ICurrentUser current) : IAuthService
{
    // Verified against when the user does not exist so that response time does not reveal valid usernames.
    private readonly string _dummyHash = passwords.Hash("niss-dummy-password-for-timing");

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var username = request.Username.Trim();
        var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower(), ct);
        var ok = passwords.Verify(user?.PasswordHash ?? _dummyHash, request.Password, out var needsRehash);
        if (user is null || !user.Enabled || !ok)
        {
            audit.RecordAs(user?.Id, user?.Username, AuditActions.LoginFailed, username, user is { Enabled: false } ? "account disabled" : "bad credentials");
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedAppException("Invalid username or password.");
        }

        if (needsRehash) user.PasswordHash = passwords.Hash(request.Password);
        user.LastLoginAt = clock.GetUtcNow();
        audit.RecordAs(user.Id, user.Username, AuditActions.Login, user.Username);
        var response = Issue(user);
        await db.SaveChangesAsync(ct);
        return response;
    }

    public async Task<AuthResponse> RefreshAsync(RefreshRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var hash = hasher.Hash(request.RefreshToken.Trim());
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct) ?? throw new UnauthorizedAppException("Invalid refresh token.");

        if (token.RevokedAt is not null)
        {
            // A rotated token was presented again: assume theft and end every session of that user.
            var live = await db.RefreshTokens.Where(t => t.AdminUserId == token.AdminUserId && t.RevokedAt == null).ToListAsync(ct);
            foreach (var t in live) t.RevokedAt = now;
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedAppException("Invalid refresh token.");
        }
        if (token.ExpiresAt <= now) throw new UnauthorizedAppException("Refresh token expired.");

        var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Id == token.AdminUserId, ct);
        if (user is null || !user.Enabled) throw new UnauthorizedAppException("Account is disabled.");

        token.RevokedAt = now;
        var response = Issue(user, rotatedFrom: token);
        await db.SaveChangesAsync(ct);
        return response;
    }

    public async Task LogoutAsync(string? refreshToken, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            var hash = hasher.Hash(refreshToken.Trim());
            var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.AdminUserId == current.UserId, ct);
            if (token is not null) token.RevokedAt ??= now;
        }
        audit.Record(AuditActions.Logout, current.Username);
        await db.SaveChangesAsync(ct);
    }

    public async Task<UserInfo> MeAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.AdminUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.Enabled, ct) ?? throw new UnauthorizedAppException("User no longer exists.");
        return ToInfo(user);
    }

    private AuthResponse Issue(AdminUser user, RefreshToken? rotatedFrom = null)
    {
        var now = clock.GetUtcNow();
        var info = ToInfo(user);
        var (access, expires) = issuer.Issue(info);
        var raw = hasher.GenerateSecret();
        var refresh = new RefreshToken
        {
            Id = Guid.NewGuid(), AdminUserId = user.Id, TokenHash = hasher.Hash(raw), CreatedAt = now,
            ExpiresAt = now.AddHours(Math.Max(1, options.Value.RefreshTokenHours)),
        };
        db.RefreshTokens.Add(refresh);
        if (rotatedFrom is not null) rotatedFrom.ReplacedById = refresh.Id;
        return new AuthResponse(access, raw, expires, info);
    }

    internal static UserInfo ToInfo(AdminUser u) => new(u.Id, u.Username, u.DisplayName, u.Role, RbacMapping.EffectivePageAccess(u));
}

/// <summary>Serialises the admin RBAC fields to/from their JSON columns and resolves effective page access.</summary>
internal static class RbacMapping
{
    public static IReadOnlyList<Guid>? ReadGuids(string? json) => Read<Guid>(json);
    public static IReadOnlyList<string>? ReadStrings(string? json) => Read<string>(json);

    private static IReadOnlyList<T>? Read<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<List<T>>(json, ProtocolJson.Options); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static string? Write<T>(IReadOnlyList<T>? values) =>
        values is null ? null : System.Text.Json.JsonSerializer.Serialize(values, ProtocolJson.Options);

    /// <summary>
    /// Reads the stored page-access map. Accepts the current object form (<c>{key: level}</c>) and, for backward
    /// compatibility, the earlier array form (a list of page keys = VIEW on each). Null/blank = no restriction.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ReadPageAccess(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json, ProtocolJson.Options);
            if (map is not null) return map;
        }
        catch (System.Text.Json.JsonException) { /* try the legacy array form */ }
        var legacy = Read<string>(json);
        return legacy?.ToDictionary(k => k, _ => PageAccess.View);
    }

    /// <summary>
    /// The effective page-access map: null (EDIT everywhere) for a SUPERADMIN or an unrestricted user; otherwise the
    /// stored map limited to known, role-reachable keys. Levels above the role's reach are dropped.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? EffectivePageAccess(AdminUser u)
    {
        if (u.Role == Roles.SuperAdmin) return null;
        var stored = ReadPageAccess(u.AllowedPagesJson);
        if (stored is null) return null;
        var result = new Dictionary<string, string>();
        foreach (var (key, level) in stored)
        {
            var page = AppPages.Find(key);
            if (page is null || !Roles.AtLeast(u.Role, page.MinRole)) continue;
            if (PageAccess.IsLevel(level) && level != PageAccess.Hidden) result[key] = level;
        }
        return result;
    }

    /// <summary>Normalises an incoming access map for storage: known role-reachable keys only, HIDDEN dropped. Null stays null.</summary>
    public static string? NormalizePageAccess(IReadOnlyDictionary<string, string>? map, string role)
    {
        if (map is null || role == Roles.SuperAdmin) return null;
        var cleaned = new Dictionary<string, string>();
        foreach (var (key, level) in map)
        {
            var page = AppPages.Find(key);
            if (page is null || !Roles.AtLeast(role, page.MinRole)) continue;
            if (level is PageAccess.View or PageAccess.Edit) cleaned[key] = level;
        }
        return System.Text.Json.JsonSerializer.Serialize(cleaned, ProtocolJson.Options);
    }

    /// <summary>
    /// The access level for one page/sub-page given a resolved map (null = unrestricted = EDIT). A sub-page with no
    /// explicit entry inherits its parent's level; a top-level page with no entry is HIDDEN.
    /// </summary>
    public static string LevelOf(IReadOnlyDictionary<string, string>? map, string key)
    {
        if (map is null) return PageAccess.Edit; // unrestricted
        if (map.TryGetValue(key, out var level)) return level;
        var page = AppPages.Find(key);
        if (page?.Parent is { } parent && map.TryGetValue(parent, out var pLevel)) return pLevel;
        return PageAccess.Hidden;
    }
}

/// <summary>SUPERADMIN-only administration of dashboard users.</summary>
public interface IAdminUserService
{
    Task<IReadOnlyList<AdminUserDto>> ListAsync(CancellationToken ct);
    Task<AdminUserDto> CreateAsync(AdminUserUpsert request, CancellationToken ct);
    Task<AdminUserDto> UpdateAsync(Guid id, AdminUserUpsert request, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}

internal sealed class AdminUserService(IAppDbContext db, IPasswordService passwords, IAuditService audit, TimeProvider clock, ICurrentUser current) : IAdminUserService
{
    public async Task<IReadOnlyList<AdminUserDto>> ListAsync(CancellationToken ct)
    {
        // Materialise first: the RBAC columns are JSON that is deserialised in memory, not translatable to SQL.
        var rows = await db.AdminUsers.AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<AdminUserDto> CreateAsync(AdminUserUpsert r, CancellationToken ct)
    {
        var username = r.Username.Trim();
        if (await db.AdminUsers.AnyAsync(u => u.Username.ToLower() == username.ToLower(), ct)) throw new ConflictException($"User {username} already exists.");
        if (string.IsNullOrEmpty(r.Password)) throw new BadRequestException("Password is required when creating a user.", nameof(r.Password));
        var role = r.Role.Trim().ToUpperInvariant();
        var user = new AdminUser
        {
            Id = Guid.NewGuid(), Username = username, DisplayName = r.DisplayName.Trim(), Role = role, Enabled = r.Enabled,
            PasswordHash = passwords.Hash(r.Password), CreatedAt = clock.GetUtcNow(),
        };
        ApplyScope(user, r);
        db.AdminUsers.Add(user);
        audit.Record(AuditActions.AdminUserCreated, username, $"role {user.Role}");
        await db.SaveChangesAsync(ct);
        return ToDto(user);
    }

    public async Task<AdminUserDto> UpdateAsync(Guid id, AdminUserUpsert r, CancellationToken ct)
    {
        var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("User not found.");
        var username = r.Username.Trim();
        if (await db.AdminUsers.AnyAsync(u => u.Id != id && u.Username.ToLower() == username.ToLower(), ct)) throw new ConflictException($"User {username} already exists.");
        var role = r.Role.Trim().ToUpperInvariant();

        var losesSuperAdmin = user.Role == Roles.SuperAdmin && user.Enabled && (role != Roles.SuperAdmin || !r.Enabled);
        if (losesSuperAdmin && !await db.AdminUsers.AnyAsync(u => u.Id != id && u.Role == Roles.SuperAdmin && u.Enabled, ct))
            throw new ConflictException("At least one enabled SUPERADMIN must remain.");

        user.Username = username; user.DisplayName = r.DisplayName.Trim(); user.Role = role; user.Enabled = r.Enabled;
        ApplyScope(user, r);
        var passwordChanged = !string.IsNullOrEmpty(r.Password);
        if (passwordChanged)
        {
            user.PasswordHash = passwords.Hash(r.Password!);
            var tokens = await db.RefreshTokens.Where(t => t.AdminUserId == id && t.RevokedAt == null).ToListAsync(ct);
            foreach (var t in tokens) t.RevokedAt = clock.GetUtcNow();
        }
        audit.Record(AuditActions.AdminUserUpdated, username, $"role {role}, enabled {r.Enabled}{(passwordChanged ? ", password changed" : "")}");
        await db.SaveChangesAsync(ct);
        return ToDto(user);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("User not found.");
        if (current.UserId == id) throw new ConflictException("You cannot delete your own account.");
        if (user.Role == Roles.SuperAdmin && !await db.AdminUsers.AnyAsync(u => u.Id != id && u.Role == Roles.SuperAdmin && u.Enabled, ct))
            throw new ConflictException("At least one enabled SUPERADMIN must remain.");
        db.RefreshTokens.RemoveRange(await db.RefreshTokens.Where(t => t.AdminUserId == id).ToListAsync(ct));
        db.AdminUsers.Remove(user);
        audit.Record(AuditActions.AdminUserDeleted, user.Username);
        await db.SaveChangesAsync(ct);
    }

    private static AdminUserDto ToDto(AdminUser u) => new(
        u.Id, u.Username, u.DisplayName, u.Role, u.Enabled, u.CreatedAt, u.LastLoginAt,
        RbacMapping.ReadPageAccess(u.AllowedPagesJson),
        u.Role == Roles.SuperAdmin || u.ScopeAllEmployees,
        RbacMapping.ReadStrings(u.AllowedDepartmentsJson) ?? [],
        RbacMapping.ReadGuids(u.AllowedEmployeeIdsJson) ?? []);

    /// <summary>Writes the access-control fields from an upsert. A SUPERADMIN is always unrestricted, so scoping is cleared.</summary>
    private static void ApplyScope(AdminUser user, AdminUserUpsert r)
    {
        if (user.Role == Roles.SuperAdmin)
        {
            user.AllowedPagesJson = null;
            user.ScopeAllEmployees = true;
            user.AllowedDepartmentsJson = null;
            user.AllowedEmployeeIdsJson = null;
            return;
        }
        user.AllowedPagesJson = RbacMapping.NormalizePageAccess(r.PageAccess, user.Role);
        user.ScopeAllEmployees = r.ScopeAllEmployees;
        if (r.ScopeAllEmployees)
        {
            user.AllowedDepartmentsJson = null;
            user.AllowedEmployeeIdsJson = null;
        }
        else
        {
            var depts = r.AllowedDepartments?.Select(d => d.Trim()).Where(d => d.Length > 0).Distinct().ToList();
            var emps = r.AllowedEmployeeIds?.Distinct().ToList();
            user.AllowedDepartmentsJson = RbacMapping.Write(depts);
            user.AllowedEmployeeIdsJson = RbacMapping.Write(emps);
        }
    }
}
