using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NISS.Application.Abstractions;
using NISS.Domain.Entities;

namespace NISS.Infrastructure.Persistence;

/// <summary>EF Core context. Table/column names are snake_case; all timestamps are stored as UTC <c>timestamptz</c>.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    /// <summary>Name of the Postgres sequence that numbers devices (PC-001, ...).</summary>
    public const string DeviceNumberSequence = "device_number_seq";

    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceUser> DeviceUsers => Set<DeviceUser>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<ProcessEvent> ProcessEvents => Set<ProcessEvent>();
    public DbSet<ProcessSession> ProcessSessions => Set<ProcessSession>();
    public DbSet<ApplicationSession> ApplicationSessions => Set<ApplicationSession>();
    public DbSet<InputActivity> InputActivities => Set<InputActivity>();
    public DbSet<IdleSession> IdleSessions => Set<IdleSession>();
    public DbSet<SystemMetric> SystemMetrics => Set<SystemMetric>();
    public DbSet<UsbEvent> UsbEvents => Set<UsbEvent>();
    public DbSet<FileEvent> FileEvents => Set<FileEvent>();
    public DbSet<Heartbeat> Heartbeats => Set<Heartbeat>();
    public DbSet<AgentStatus> AgentStatuses => Set<AgentStatus>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();
    public DbSet<EventIngestion> EventIngestions => Set<EventIngestion>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ConfigurationPolicy> ConfigurationPolicies => Set<ConfigurationPolicy>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AppCategoryRule> AppCategoryRules => Set<AppCategoryRule>();
    public DbSet<ServerSetting> ServerSettings => Set<ServerSetting>();
    public DbSet<Screenshot> Screenshots => Set<Screenshot>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Npgsql refuses DateTimeOffset values whose offset is not zero; normalise everything to UTC on the way in.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<NullableUtcConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<long>(DeviceNumberSequence).StartsAt(1).IncrementsBy(1);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    private sealed class UtcConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(v => v.ToUniversalTime(), v => v);
    private sealed class NullableUtcConverter() : ValueConverter<DateTimeOffset?, DateTimeOffset?>(v => v.HasValue ? v.Value.ToUniversalTime() : v, v => v);
}
