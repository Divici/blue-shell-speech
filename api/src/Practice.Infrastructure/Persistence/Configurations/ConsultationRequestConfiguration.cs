using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Practice.Domain.Consultations;
using Practice.Domain.Patients;

namespace Practice.Infrastructure.Persistence.Configurations;

public sealed class ConsultationRequestConfiguration
    : IEntityTypeConfiguration<ConsultationRequest>
{
    public void Configure(EntityTypeBuilder<ConsultationRequest> builder)
    {
        builder.ToTable("ConsultationRequests");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedOnAdd();
        builder.HasIndex(c => c.PublicId).IsUnique();

        builder.Property(c => c.ProviderId).IsRequired();
        builder.Property(c => c.SubmittedAtUtc).IsRequired();

        /*
         * THE COLUMN WIDTHS ARE THE SAME NUMBERS THE AGGREGATE ENFORCES.
         *
         * Deliberately not wider "to be safe". A column with more room than the aggregate
         * allows is a second, quieter limit that only a raw INSERT can reach — and the one
         * thing worse than a truncated description of a child is a truncated description
         * of a child that nothing refused.
         */
        builder.Property(c => c.ParentName)
            .HasMaxLength(ConsultationRequest.MaxParentNameLength).IsRequired();
        builder.Property(c => c.Email)
            .HasMaxLength(ConsultationRequest.MaxEmailLength).IsRequired();
        builder.Property(c => c.Phone)
            .HasMaxLength(ConsultationRequest.MaxPhoneLength);
        builder.Property(c => c.ChildFirstName)
            .HasMaxLength(ConsultationRequest.MaxChildFirstNameLength).IsRequired();
        builder.Property(c => c.ChildAgeMonths).IsRequired();
        builder.Property(c => c.Concerns)
            .HasMaxLength(ConsultationRequest.MaxConcernsLength).IsRequired();

        builder.Property(c => c.PreferredContactMethod).IsRequired();
        builder.Property(c => c.Status).IsRequired();

        /*
         * char(64), not nvarchar(max).
         *
         * A SHA-256 hex digest is exactly 64 characters from an alphabet of 16, so the
         * column says what the value is. A wide, variable, Unicode column would accept an
         * IP address, a User-Agent, or a paragraph — and the whole point of this column is
         * that the visitor's address is NOT in it (docs/DATA_MODEL.md).
         */
        builder.Property(c => c.SourceIpHash).HasColumnType("char(64)");

        builder.Property(c => c.RowVersion).IsRowVersion();

        /*
         * The triage query: this provider's enquiries, newest first, usually filtered to
         * the ones nobody has answered yet.
         */
        builder.HasIndex(c => new { c.ProviderId, c.Status, c.SubmittedAtUtc });

        /*
         * A real foreign key, not a loose long.
         *
         * ConvertedPatientId is the only link between an enquiry and the caseload, and it
         * is what answers "where did this family come from" a year later. Restrict rather
         * than Cascade because a patient is never hard-deleted here (docs/DATA_MODEL.md),
         * so a cascade would be a path that exists only to be wrong.
         *
         * No navigation property: the enquiry is not part of the Patient aggregate, and
         * giving it one would invite a load that crosses from a public-intake row into
         * clinical data.
         */
        builder.HasOne<Patient>()
            .WithMany()
            .HasForeignKey(c => c.ConvertedPatientId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
