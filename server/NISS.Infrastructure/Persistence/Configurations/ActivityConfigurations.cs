using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NISS.Domain;
using NISS.Domain.Entities;

namespace NISS.Infrastructure.Persistence.Configurations;

/// <summary>Shared mapping for tables that belong to a device: uuid key, FK to devices, time-range indexes.</summary>
internal static class DeviceOwned
{
    public static void Map<T>(EntityTypeBuilder<T> b, string table, string timeColumnProperty) where T : class
    {
        b.ToTable(table);
        b.HasOne<Device>().WithMany().HasForeignKey("DeviceId").OnDelete(DeleteBehavior.Cascade);
        b.HasIndex("DeviceId", timeColumnProperty);
        b.HasIndex(timeColumnProperty);
    }
}

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> b)
    {
        DeviceOwned.Map(b, "user_sessions", nameof(UserSession.LoginAt));
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();
        b.HasIndex(s => new { s.EmployeeId, s.LoginAt });
        b.HasIndex(s => s.DeviceId).HasFilter("logout_at IS NULL").HasDatabaseName("ix_user_sessions_open");
    }
}

internal sealed class ProcessEventConfiguration : IEntityTypeConfiguration<ProcessEvent>
{
    public void Configure(EntityTypeBuilder<ProcessEvent> b)
    {
        DeviceOwned.Map(b, "process_events", nameof(ProcessEvent.Timestamp));
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedNever();
    }
}

internal sealed class ProcessSessionConfiguration : IEntityTypeConfiguration<ProcessSession>
{
    public void Configure(EntityTypeBuilder<ProcessSession> b)
    {
        DeviceOwned.Map(b, "process_sessions", nameof(ProcessSession.StartedAt));
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();
        b.HasIndex(s => s.ProcessName);
        b.HasIndex(s => s.EmployeeId);
        b.HasIndex(s => s.DeviceId).HasFilter("ended_at IS NULL").HasDatabaseName("ix_process_sessions_running");
    }
}

internal sealed class ApplicationSessionConfiguration : IEntityTypeConfiguration<ApplicationSession>
{
    public void Configure(EntityTypeBuilder<ApplicationSession> b)
    {
        DeviceOwned.Map(b, "application_sessions", nameof(ApplicationSession.StartedAt));
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();
        b.Property(s => s.SessionType).HasDefaultValue("FOREGROUND");
        b.HasIndex(s => new { s.EmployeeId, s.StartedAt });
        b.HasIndex(s => s.ProcessName);
        b.HasIndex(s => s.DeviceId).HasFilter("ended_at IS NULL").HasDatabaseName("ix_application_sessions_open");
    }
}

internal sealed class InputActivityConfiguration : IEntityTypeConfiguration<InputActivity>
{
    public void Configure(EntityTypeBuilder<InputActivity> b)
    {
        DeviceOwned.Map(b, "input_activity", nameof(InputActivity.Timestamp));
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedNever();
        b.HasIndex(a => new { a.DeviceId, a.Timestamp }).IsUnique().HasDatabaseName("ux_input_activity_device_bucket");
    }
}

internal sealed class IdleSessionConfiguration : IEntityTypeConfiguration<IdleSession>
{
    public void Configure(EntityTypeBuilder<IdleSession> b)
    {
        DeviceOwned.Map(b, "idle_sessions", nameof(IdleSession.StartedAt));
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();
        b.HasIndex(s => new { s.DeviceId, s.Kind }).HasFilter("ended_at IS NULL").HasDatabaseName("ix_idle_sessions_open");
        b.HasIndex(s => new { s.EmployeeId, s.StartedAt });
    }
}

internal sealed class SystemMetricConfiguration : IEntityTypeConfiguration<SystemMetric>
{
    public void Configure(EntityTypeBuilder<SystemMetric> b)
    {
        DeviceOwned.Map(b, "system_metrics", nameof(SystemMetric.Timestamp));
        b.HasKey(m => m.Id);
        b.Property(m => m.Id).ValueGeneratedNever();
        b.Property(m => m.DisksJson).HasColumnType("jsonb");
    }
}

internal sealed class UsbEventConfiguration : IEntityTypeConfiguration<UsbEvent>
{
    public void Configure(EntityTypeBuilder<UsbEvent> b)
    {
        DeviceOwned.Map(b, "usb_events", nameof(UsbEvent.Timestamp));
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedNever();
        b.HasIndex(e => e.EmployeeId);
    }
}

internal sealed class FileEventConfiguration : IEntityTypeConfiguration<FileEvent>
{
    public void Configure(EntityTypeBuilder<FileEvent> b)
    {
        DeviceOwned.Map(b, "file_events", nameof(FileEvent.Timestamp));
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedNever();
        b.HasIndex(e => e.EmployeeId);
    }
}

internal sealed class HeartbeatConfiguration : IEntityTypeConfiguration<Heartbeat>
{
    public void Configure(EntityTypeBuilder<Heartbeat> b)
    {
        DeviceOwned.Map(b, "heartbeats", nameof(Heartbeat.Timestamp));
        b.HasKey(h => h.Id);
        b.Property(h => h.Id).ValueGeneratedNever();
    }
}

internal sealed class AgentStatusConfiguration : IEntityTypeConfiguration<AgentStatus>
{
    public void Configure(EntityTypeBuilder<AgentStatus> b)
    {
        b.ToTable("agent_status");
        b.HasKey(s => s.DeviceId);
        b.Property(s => s.DeviceId).ValueGeneratedNever();
        b.Property(s => s.OpenApplicationsJson).HasColumnType("jsonb");
        b.Property(s => s.SessionAgentsJson).HasColumnType("jsonb");
        b.Property(s => s.CollectorsJson).HasColumnType("jsonb");
        b.HasOne<Device>().WithOne().HasForeignKey<AgentStatus>(s => s.DeviceId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SecurityEventConfiguration : IEntityTypeConfiguration<SecurityEvent>
{
    public void Configure(EntityTypeBuilder<SecurityEvent> b)
    {
        b.ToTable("security_events");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedNever();
        b.Property(e => e.Severity).HasConversion(v => v.ToString().ToUpperInvariant(), s => Enum.Parse<SecuritySeverity>(s, true));
        b.HasIndex(e => e.RaisedAt);
        b.HasIndex(e => new { e.DeviceId, e.RaisedAt });
        b.HasIndex(e => new { e.DeviceId, e.Kind }).HasFilter("resolved_at IS NULL").HasDatabaseName("ix_security_events_open");
        b.HasOne<Device>().WithMany().HasForeignKey(e => e.DeviceId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class EventIngestionConfiguration : IEntityTypeConfiguration<EventIngestion>
{
    public void Configure(EntityTypeBuilder<EventIngestion> b)
    {
        DeviceOwned.Map(b, "event_ingestion", nameof(EventIngestion.Timestamp));
        b.HasKey(e => e.EventId);
        b.Property(e => e.EventId).ValueGeneratedNever();
        b.Property(e => e.Payload).HasColumnType("jsonb");
        b.HasIndex(e => new { e.DeviceId, e.EventType, e.Timestamp });
        b.HasIndex(e => new { e.EmployeeId, e.Timestamp });
    }
}
