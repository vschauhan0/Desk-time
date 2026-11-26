namespace NISS.Domain.Entities;

/// <summary>Dashboard user.</summary>
public sealed class AdminUser
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "VIEWER";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}

/// <summary>Rotating refresh token (only the hash is stored).</summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid AdminUserId { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
}

/// <summary>Audit trail of admin actions.</summary>
public sealed class AuditLog
{
    public Guid Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public Guid? AdminUserId { get; set; }
    public string? Username { get; set; }
    public string Action { get; set; } = "";
    public string? Target { get; set; }
    public string? Detail { get; set; }
    public string? IpAddress { get; set; }
}

/// <summary>Monitoring policy for a scope. The policy body is stored as JSON (<c>AgentPolicy</c>).</summary>
public sealed class ConfigurationPolicy
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public PolicyScope Scope { get; set; }
    /// <summary>Department name or device code; empty for the global policy.</summary>
    public string ScopeValue { get; set; } = "";
    public string PolicyJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One-time enrollment token (only the hash is stored).</summary>
public sealed class EnrollmentToken
{
    public Guid Id { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public string? EmployeeCode { get; set; }
    public Guid? BoundDeviceId { get; set; }
    /// <summary>When the code stops working. Null = never expires (valid until used).</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public Guid? UsedByDeviceId { get; set; }
    public string? Note { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Application categorisation rule with a simple wildcard pattern (e.g. <c>chrome*</c>).</summary>
public sealed class AppCategoryRule
{
    public Guid Id { get; set; }
    public string Pattern { get; set; } = "";
    public string Category { get; set; } = "Unknown";
    public int Priority { get; set; }
}

/// <summary>Key/value server setting (retention, thresholds, generated secrets).</summary>
public sealed class ServerSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}
