using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Practice.Domain.Scheduling;

namespace Practice.Infrastructure.Persistence.Configurations;

public sealed class AppointmentConfiguration : IEntityTypeConfiguration<Appointment>
{
    public void Configure(EntityTypeBuilder<Appointment> builder)
    {
        builder.ToTable("Appointments");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedOnAdd();
        builder.HasIndex(a => a.PublicId).IsUnique();

        builder.Property(a => a.ProviderId).IsRequired();
        builder.Property(a => a.PatientId).IsRequired();
        builder.Property(a => a.AppointmentType).IsRequired();
        builder.Property(a => a.StartUtc).IsRequired();
        builder.Property(a => a.DurationMinutes).IsRequired();
        builder.Property(a => a.Status).IsRequired();
        builder.Property(a => a.Notes).HasMaxLength(1000);
        builder.Property(a => a.CancellationReason).HasMaxLength(500);
        builder.Property(a => a.RowVersion).IsRowVersion();

        // decimal(6,1): up to 99999.9 miles, one decimal place. Enough for a lifetime of
        // in-home visits, and float would make a mileage total not add up.
        builder.Property(a => a.Mileage).HasColumnType("decimal(6,1)");

        // Computed from StartUtc + DurationMinutes; never stored (see Appointment).
        builder.Ignore(a => a.EndUtc);

        /*
         * The daily view is the query this table exists for: "what am I doing today,
         * in order". Provider first so the tenancy filter is satisfied by the index.
         */
        builder.HasIndex(a => new { a.ProviderId, a.StartUtc });
        builder.HasIndex(a => new { a.PatientId, a.StartUtc });
    }
}
