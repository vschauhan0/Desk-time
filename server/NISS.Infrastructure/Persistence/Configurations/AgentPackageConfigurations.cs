using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NISS.Domain.Entities;

namespace NISS.Infrastructure.Persistence.Configurations;

internal sealed class AgentPackageConfiguration : IEntityTypeConfiguration<AgentPackage>
{
    public void Configure(EntityTypeBuilder<AgentPackage> b)
    {
        b.ToTable("agent_packages");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Version).HasMaxLength(64);
        b.HasIndex(x => x.Version).IsUnique();
        b.Property(x => x.RelPath).HasMaxLength(512);
        b.Property(x => x.Sha256).HasMaxLength(64);
        b.Property(x => x.SignerSubject).HasMaxLength(512);
        b.Property(x => x.UploadedBy).HasMaxLength(256);
        b.Property(x => x.Notes).HasMaxLength(2000);
    }
}
