using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NISS.Domain.Entities;

namespace NISS.Infrastructure.Persistence.Configurations;

internal sealed class ScreenshotConfiguration : IEntityTypeConfiguration<Screenshot>
{
    public void Configure(EntityTypeBuilder<Screenshot> b)
    {
        DeviceOwned.Map(b, "screenshots", nameof(Screenshot.CapturedAtUtc));
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();
        b.Property(s => s.SessionUser).HasMaxLength(256);
        b.Property(s => s.RelPath).HasMaxLength(512);
        b.Property(s => s.FrameId).HasMaxLength(128);
        b.HasIndex(s => new { s.DeviceId, s.FrameId }).IsUnique();
        b.HasIndex(s => s.EmployeeId);
    }
}
