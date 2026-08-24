using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Practice.Domain.Providers;

namespace Practice.Infrastructure.Persistence.Configurations;

public sealed class ProviderConfiguration : IEntityTypeConfiguration<Provider>
{
    public void Configure(EntityTypeBuilder<Provider> builder)
    {
        builder.ToTable("Providers");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();

        /*
         * PublicId is the only identifier that leaves the server. Unique and indexed
         * because every lookup from a URL resolves through it, and NOT the clustered key
         * — a GUID clustered index fragments badly on insert (docs/DATA_MODEL.md).
         */
        builder.Property(p => p.PublicId).IsRequired();
        builder.HasIndex(p => p.PublicId).IsUnique();

        builder.Property(p => p.IdentityUserId).HasMaxLength(450).IsRequired();
        builder.HasIndex(p => p.IdentityUserId).IsUnique();

        builder.Property(p => p.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Credentials).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Npi).HasMaxLength(10);
        builder.Property(p => p.LicenseNumber).HasMaxLength(50).IsRequired();

        // Fixed-width: a state code is always exactly two characters.
        builder.Property(p => p.LicenseState).HasColumnType("char(2)").IsRequired();

        builder.Property(p => p.IsActive).IsRequired();
        builder.Property(p => p.CreatedAtUtc).IsRequired();
        builder.Property(p => p.CreatedBy).HasMaxLength(450);

        // rowversion: optimistic concurrency without a version column to maintain by hand.
        builder.Property(p => p.RowVersion).IsRowVersion();
    }
}
