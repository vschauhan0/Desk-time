using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NISS.Domain.Entities;

namespace NISS.Infrastructure.Persistence.Configurations;

internal sealed class DeviceInventoryConfiguration : IEntityTypeConfiguration<DeviceInventory>
{
    public void Configure(EntityTypeBuilder<DeviceInventory> b)
    {
        b.ToTable("device_inventories");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        // One snapshot row per device (latest wins).
        b.HasIndex(x => x.DeviceId).IsUnique();
        b.Property(x => x.Manufacturer).HasMaxLength(256);
        b.Property(x => x.Model).HasMaxLength(256);
        b.Property(x => x.Serial).HasMaxLength(256);
        b.Property(x => x.OsName).HasMaxLength(128);
        b.Property(x => x.OsVersion).HasMaxLength(64);
        b.Property(x => x.OsBuild).HasMaxLength(64);
        b.Property(x => x.CpuModel).HasMaxLength(256);
        // Installed-software list lives in a jsonb column, not a child table.
        b.OwnsMany(x => x.Software, o => o.ToJson());
    }
}
