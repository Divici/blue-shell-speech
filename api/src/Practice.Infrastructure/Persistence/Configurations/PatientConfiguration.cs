using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Practice.Domain.Patients;

namespace Practice.Infrastructure.Persistence.Configurations;

public sealed class PatientConfiguration : IEntityTypeConfiguration<Patient>
{
    public void Configure(EntityTypeBuilder<Patient> builder)
    {
        builder.ToTable("Patients");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();
        builder.HasIndex(p => p.PublicId).IsUnique();

        builder.Property(p => p.ProviderId).IsRequired();
        builder.Property(p => p.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(p => p.LastName).HasMaxLength(100).IsRequired();

        // `date`, not datetime2: a birthdate has no time and no timezone. Storing it as a
        // timestamp invites an off-by-one when rendered in a different zone.
        builder.Property(p => p.DateOfBirth).HasColumnType("date").IsRequired();

        builder.Property(p => p.Status).IsRequired();
        builder.Property(p => p.ClinicalSummary).HasMaxLength(4000);
        builder.Property(p => p.RowVersion).IsRowVersion();

        /*
         * Name search is the most-used interaction in the app, so it is indexed —
         * and it is scoped by ProviderId first so the filter is satisfied by the index
         * rather than after it.
         *
         * This index is why Always Encrypted was rejected for these columns (D012):
         * deterministic encryption permits equality only, and nobody searches by exact
         * full surname.
         */
        builder.HasIndex(p => new { p.ProviderId, p.LastName, p.FirstName });
        builder.HasIndex(p => new { p.ProviderId, p.Status });

        builder.HasMany(p => p.Guardians)
            .WithOne()
            .HasForeignKey(g => g.PatientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(p => p.Addresses)
            .WithOne()
            .HasForeignKey(a => a.PatientId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(p => p.Guardians).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(p => p.Addresses).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class GuardianConfiguration : IEntityTypeConfiguration<Guardian>
{
    public void Configure(EntityTypeBuilder<Guardian> builder)
    {
        builder.ToTable("Guardians");

        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedOnAdd();
        builder.HasIndex(g => g.PublicId).IsUnique();

        builder.Property(g => g.ProviderId).IsRequired();
        builder.Property(g => g.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(g => g.LastName).HasMaxLength(100).IsRequired();
        builder.Property(g => g.Relationship).HasMaxLength(50).IsRequired();
        builder.Property(g => g.Phone).HasMaxLength(50);
        builder.Property(g => g.Email).HasMaxLength(256);
        builder.Property(g => g.RowVersion).IsRowVersion();

        /*
         * At most one primary contact per patient, enforced by the DATABASE.
         *
         * The aggregate demotes the previous primary on add, but a filtered unique index
         * is what makes it true against a concurrent write or a hand-run script. Two
         * "primary" numbers in a custody situation is not a tie the reader should break.
         */
        builder.HasIndex(g => g.PatientId)
            .HasFilter("[IsPrimaryContact] = 1")
            .IsUnique()
            .HasDatabaseName("UX_Guardians_OnePrimaryPerPatient");
    }
}

public sealed class PatientAddressConfiguration : IEntityTypeConfiguration<PatientAddress>
{
    public void Configure(EntityTypeBuilder<PatientAddress> builder)
    {
        builder.ToTable("PatientAddresses");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedOnAdd();
        builder.HasIndex(a => a.PublicId).IsUnique();

        builder.Property(a => a.ProviderId).IsRequired();
        builder.Property(a => a.Line1).HasMaxLength(200).IsRequired();
        builder.Property(a => a.Line2).HasMaxLength(200);
        builder.Property(a => a.City).HasMaxLength(100).IsRequired();
        builder.Property(a => a.State).HasColumnType("char(2)").IsRequired();
        builder.Property(a => a.PostalCode).HasMaxLength(20).IsRequired();
        builder.Property(a => a.AddressType).IsRequired();
        builder.Property(a => a.Notes).HasMaxLength(500);
        builder.Property(a => a.EffectiveFrom).HasColumnType("date").IsRequired();
        builder.Property(a => a.EffectiveTo).HasColumnType("date");
        builder.Property(a => a.RowVersion).IsRowVersion();

        builder.Ignore(a => a.IsCurrent);

        builder.HasIndex(a => new { a.PatientId, a.AddressType });
    }
}
