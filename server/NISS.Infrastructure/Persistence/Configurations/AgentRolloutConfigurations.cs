using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NISS.Domain.Entities;

namespace NISS.Infrastructure.Persistence.Configurations;

internal sealed class AgentRolloutConfiguration : IEntityTypeConfiguration<AgentRollout>
{
    public void Configure(EntityTypeBuilder<AgentRollout> b)
    {
        b.ToTable("agent_rollouts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Version).HasMaxLength(64);
        b.Property(x => x.State).HasMaxLength(16);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.CreatedBy).HasMaxLength(256);
        b.HasIndex(x => x.State);
    }
}

internal sealed class DeviceUpdateStateConfiguration : IEntityTypeConfiguration<DeviceUpdateState>
{
    public void Configure(EntityTypeBuilder<DeviceUpdateState> b)
    {
        b.ToTable("device_update_states");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasIndex(x => x.DeviceId).IsUnique();
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        b.Property(x => x.AssignedVersion).HasMaxLength(64);
        b.Property(x => x.FailedVersion).HasMaxLength(64);
        b.Property(x => x.PackageServedVersion).HasMaxLength(64);
        b.HasIndex(x => x.FailedVersion);
        b.Property(x => x.Phase).HasMaxLength(16);
        b.Property(x => x.LastError).HasMaxLength(1000);
    }
}
