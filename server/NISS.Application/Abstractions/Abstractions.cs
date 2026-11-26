using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NISS.Domain.Entities;
using NISS.Monitor.Protocol;

namespace NISS.Application.Abstractions;

/// <summary>Unit-of-work / query root implemented by the EF Core context in the infrastructure layer.</summary>
public interface IAppDbContext
{
    DbSet<Employee> Employees { get; }
    DbSet<Device> Devices { get; }
    DbSet<DeviceUser> DeviceUsers { get; }
    DbSet<UserSession> UserSessions { get; }
    DbSet<ProcessEvent> ProcessEvents { get; }
    DbSet<ProcessSession> ProcessSessions { get; }
    DbSet<ApplicationSession> ApplicationSessions { get; }
    DbSet<InputActivity> InputActivities { get; }
    DbSet<IdleSession> IdleSessions { get; }
    DbSet<SystemMetric> SystemMetrics { get; }
    DbSet<UsbEvent> UsbEvents { get; }
    DbSet<FileEvent> FileEvents { get; }
    DbSet<Heartbeat> Heartbeats { get; }
    DbSet<AgentStatus> AgentStatuses { get; }
    DbSet<SecurityEvent> SecurityEvents { get; }
    DbSet<EventIngestion> EventIngestions { get; }
    DbSet<AdminUser> AdminUsers { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<ConfigurationPolicy> ConfigurationPolicies { get; }
    DbSet<EnrollmentToken> EnrollmentTokens { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<Domain.Entities.AppCategoryRule> AppCategoryRules { get; }
    DbSet<ServerSetting> ServerSettings { get; }
    DbSet<Screenshot> Screenshots { get; }

    DatabaseFacade Database { get; }
    ChangeTracker ChangeTracker { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Database operations that need provider specific SQL (idempotent insert, upserts, sequences, batched deletes).</summary>
public interface IIngestionStore
{
    /// <summary>Serialise concurrent ingestion for one device for the lifetime of the current transaction.</summary>
    Task LockDeviceAsync(string deviceCode, CancellationToken ct);

    /// <summary><c>INSERT ... ON CONFLICT (event_id) DO NOTHING</c>. Returns false when the event was already stored.</summary>
    Task<bool> TryInsertAsync(EventIngestion row, CancellationToken ct);

    /// <summary>Merge-upsert of one input bucket (counters keep the maximum, optional fields are coalesced).</summary>
    Task UpsertInputActivityAsync(InputActivity row, CancellationToken ct);
}

/// <summary>Allocates the numeric part of device ids (PC-001, PC-002, ...).</summary>
public interface IDeviceNumberSequence
{
    Task<long> NextAsync(CancellationToken ct);
}

/// <summary>Batched deletes used by the retention job.</summary>
public interface IRetentionStore
{
    /// <summary>Delete rows of a known retention target older than the cutoff, in batches. Returns the number deleted.</summary>
    Task<long> DeleteOlderThanAsync(RetentionTarget target, DateTimeOffset cutoff, CancellationToken ct);
}

/// <summary>Tables that the retention job prunes.</summary>
public enum RetentionTarget
{
    EventIngestion, ProcessEvents, InputActivity, IdleSessions, FileEvents, UsbEvents,
    ApplicationSessions, ProcessSessions, UserSessions,
    SystemMetrics, Heartbeats, SecurityEvents,
    AuditLogs, RefreshTokens,
}

/// <summary>Pushes changes to connected dashboards. Implemented with SignalR in the API project.</summary>
public interface IRealtimeNotifier
{
    Task DeviceStateChangedAsync(LiveDeviceCard card, CancellationToken ct = default);
    Task DeviceStatusChangedAsync(DeviceStatusChange change, CancellationToken ct = default);
    Task SecurityEventRaisedAsync(SecurityEventDto dto, CancellationToken ct = default);
    Task EventIngestedAsync(EventDto dto, CancellationToken ct = default);
}

/// <summary>The authenticated dashboard user (or nobody) behind the current operation.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    string? Username { get; }
    string? Role { get; }
    string? IpAddress { get; }
}

/// <summary>Writes audit log entries for admin actions.</summary>
public interface IAuditService
{
    /// <summary>Adds an entry to the current unit of work; it is persisted by the caller's next SaveChanges.</summary>
    void Record(string action, string? target = null, string? detail = null);

    /// <summary>Adds an entry on behalf of an explicit actor (used by login, where nobody is authenticated yet).</summary>
    void RecordAs(Guid? userId, string? username, string action, string? target = null, string? detail = null);

    /// <summary>Adds an entry and saves immediately.</summary>
    Task WriteAsync(string action, string? target = null, string? detail = null, CancellationToken ct = default);
}

/// <summary>Password hashing (PBKDF2 via ASP.NET Core Identity's hasher).</summary>
public interface IPasswordService
{
    string Hash(string password);
    /// <summary>Verifies a password. <paramref name="needsRehash"/> is true when the stored hash uses outdated parameters.</summary>
    bool Verify(string hash, string password, out bool needsRehash);
}

/// <summary>Random secrets and their SHA-256 hashes (device secrets, enrollment tokens, refresh tokens).</summary>
public interface ISecretHasher
{
    /// <summary>32 random bytes, base64url encoded.</summary>
    string GenerateSecret();
    byte[] Hash(string secret);
    /// <summary>Constant-time comparison of the hash of <paramref name="secret"/> with <paramref name="expectedHash"/>.</summary>
    bool Verify(string secret, byte[] expectedHash);
}

/// <summary>Issues signed access tokens.</summary>
public interface IAccessTokenIssuer
{
    (string Token, DateTimeOffset ExpiresAtUtc) Issue(UserInfo user);
}

/// <summary>Holds the JWT signing key (from configuration or generated once and persisted).</summary>
public interface ISigningKeyProvider
{
    Task InitializeAsync(CancellationToken ct = default);
    byte[] GetKey();
}
