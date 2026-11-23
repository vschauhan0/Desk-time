using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NISS.Domain;
using NISS.Domain.Entities;

namespace NISS.Infrastructure.Persistence.Configurations;

internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> b)
    {
        b.ToTable("employees");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedNever();
        b.HasIndex(e => e.EmployeeCode).IsUnique();
        b.HasIndex(e => e.Department);
    }
}

internal sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> b)
    {
        b.ToTable("devices");
        b.HasKey(d => d.Id);
        b.Property(d => d.Id).ValueGeneratedNever();
        b.Property(d => d.DeviceCode).HasColumnName("device_id").IsRequired();
        b.HasIndex(d => d.DeviceCode).IsUnique();
        b.HasIndex(d => d.MachineGuid).IsUnique().HasFilter("machine_guid IS NOT NULL");
        b.HasIndex(d => d.Status);
        b.HasIndex(d => d.EmployeeId);
        b.Property(d => d.IpAddressesJson).HasColumnType("jsonb");
        b.Property(d => d.MacAddressesJson).HasColumnType("jsonb");
        b.HasOne(d => d.Employee).WithMany().HasForeignKey(d => d.EmployeeId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class DeviceUserConfiguration : IEntityTypeConfiguration<DeviceUser>
{
    public void Configure(EntityTypeBuilder<DeviceUser> b)
    {
        b.ToTable("device_users");
        b.HasKey(u => u.Id);
        b.Property(u => u.Id).ValueGeneratedNever();
        b.HasIndex(u => new { u.DeviceId, u.WindowsUsername }).IsUnique();
        b.HasOne<Device>().WithMany().HasForeignKey(u => u.DeviceId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Employee>().WithMany().HasForeignKey(u => u.EmployeeId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class AdminUserConfiguration : IEntityTypeConfiguration<AdminUser>
{
    public void Configure(EntityTypeBuilder<AdminUser> b)
    {
        b.ToTable("admin_users");
        b.HasKey(u => u.Id);
        b.Property(u => u.Id).ValueGeneratedNever();
        b.HasIndex(u => u.Username).IsUnique();
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("refresh_tokens");
        b.HasKey(t => t.Id);
        b.Property(t => t.Id).ValueGeneratedNever();
        b.HasIndex(t => t.TokenHash).IsUnique();
        b.HasIndex(t => t.AdminUserId);
        b.HasIndex(t => t.ExpiresAt);
        b.HasOne<AdminUser>().WithMany().HasForeignKey(t => t.AdminUserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("audit_logs");
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedNever();
        b.HasIndex(a => a.Timestamp);
        b.HasIndex(a => new { a.Action, a.Timestamp });
    }
}

internal sealed class ConfigurationPolicyConfiguration : IEntityTypeConfiguration<ConfigurationPolicy>
{
    public void Configure(EntityTypeBuilder<ConfigurationPolicy> b)
    {
        b.ToTable("configuration_policies");
        b.HasKey(p => p.Id);
        b.Property(p => p.Id).ValueGeneratedNever();
        b.Property(p => p.Scope).HasConversion(v => v.ToString().ToUpperInvariant(), s => Enum.Parse<PolicyScope>(s, true));
        b.Property(p => p.PolicyJson).HasColumnType("jsonb");
        b.HasIndex(p => new { p.Scope, p.ScopeValue }).IsUnique();
    }
}

internal sealed class EnrollmentTokenConfiguration : IEntityTypeConfiguration<EnrollmentToken>
{
    public void Configure(EntityTypeBuilder<EnrollmentToken> b)
    {
        b.ToTable("enrollment_tokens");
        b.HasKey(t => t.Id);
        b.Property(t => t.Id).ValueGeneratedNever();
        b.HasIndex(t => t.TokenHash).IsUnique();
        b.HasIndex(t => t.ExpiresAt);
    }
}

internal sealed class AppCategoryRuleConfiguration : IEntityTypeConfiguration<AppCategoryRule>
{
    public void Configure(EntityTypeBuilder<AppCategoryRule> b)
    {
        b.ToTable("app_category_rules");
        b.HasKey(r => r.Id);
        b.Property(r => r.Id).ValueGeneratedNever();
        b.HasIndex(r => r.Priority);
    }
}

internal sealed class ServerSettingConfiguration : IEntityTypeConfiguration<ServerSetting>
{
    public void Configure(EntityTypeBuilder<ServerSetting> b)
    {
        b.ToTable("server_settings");
        b.HasKey(s => s.Key);
        b.Property(s => s.Key).ValueGeneratedNever();
    }
}
