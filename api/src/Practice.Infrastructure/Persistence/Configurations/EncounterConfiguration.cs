using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Practice.Domain.Billing;
using Practice.Domain.ClinicalNotes;
using Practice.Domain.Patients;
using Practice.Domain.Providers;
using Practice.Domain.Scheduling;

namespace Practice.Infrastructure.Persistence.Configurations;

public sealed class EncounterConfiguration : IEntityTypeConfiguration<Encounter>
{
    public void Configure(EntityTypeBuilder<Encounter> builder)
    {
        builder.ToTable("Encounters");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();
        builder.HasIndex(e => e.PublicId).IsUnique();

        builder.Property(e => e.ProviderId).IsRequired();
        builder.Property(e => e.PatientId).IsRequired();
        builder.Property(e => e.AppointmentId).IsRequired();
        builder.Property(e => e.RenderingProviderId).IsRequired();

        // `date`, not datetime2: a date of service is the calendar day a payer compares
        // against, resolved in the practice's timezone at creation. See Encounter.
        builder.Property(e => e.ServiceDate).HasColumnType("date").IsRequired();

        /*
         * varchar, not nvarchar, and fixed at five.
         *
         * A CPT or HCPCS code is five ASCII letters and digits — there is no code in either
         * set that needs Unicode, and a column that accepts one accepts a homoglyph that
         * looks like a valid code on a bill and is not.
         */
        builder.Property(e => e.CptCode)
            .HasColumnType($"varchar({Encounter.CptCodeLength})").IsRequired();

        builder.Property(e => e.Modifiers)
            .HasColumnType($"varchar({Encounter.MaxModifiersLength})");

        builder.Property(e => e.PlaceOfService).IsRequired();
        builder.Property(e => e.Units).IsRequired();

        // decimal, never float. A charge and a payment that do not add up to zero because
        // of binary rounding is a conversation with a family about money.
        builder.Property(e => e.ChargeAmount).HasColumnType("decimal(10,2)").IsRequired();
        builder.Property(e => e.AmountPaid).HasColumnType("decimal(10,2)").IsRequired();

        builder.Property(e => e.PaymentStatus).IsRequired();
        builder.Property(e => e.RowVersion).IsRowVersion();

        /*
         * The superbill query: this provider's encounters over a date range, usually
         * filtered to a patient. Provider first so the tenancy filter is satisfied by the
         * index rather than after it — the same shape the Appointment indexes use.
         */
        builder.HasIndex(e => new { e.ProviderId, e.ServiceDate });
        builder.HasIndex(e => new { e.PatientId, e.ServiceDate });

        /*
         * DELIBERATELY NOT UNIQUE ON AppointmentId.
         *
         * One visit can produce two billable lines — a therapy code and a device-check code
         * on the same afternoon. ClinicalNotes has a filtered unique index on the same
         * column for the opposite reason: a visit has one current note and may have several
         * charges.
         */
        builder.HasIndex(e => e.AppointmentId);

        builder.HasOne<Patient>()
            .WithMany()
            .HasForeignKey(e => e.PatientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Appointment>()
            .WithMany()
            .HasForeignKey(e => e.AppointmentId)
            .OnDelete(DeleteBehavior.Restrict);

        /*
         * Two foreign keys to Providers, which is the point.
         *
         * ProviderId is tenancy; RenderingProviderId is who delivered the service and whose
         * NPI prints on the superbill. They hold the same value on every row this practice
         * writes today. Restrict on both: a provider is never hard-deleted, so a cascade
         * would be a path that exists only to be wrong.
         */
        builder.HasOne<Provider>()
            .WithMany()
            .HasForeignKey(e => e.RenderingProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        /*
         * A real foreign key, Restrict, and no navigation property.
         *
         * Restrict because the one deletable note is an EMPTY draft (D064), which nothing
         * would ever be coded from — so this constraint only fires on a delete that should
         * not be happening, and firing is the correct behaviour. No navigation, for the
         * reason ConsultationRequest gives: a billing row is not part of the note aggregate,
         * and a navigation invites a load that pulls four PHI columns into a superbill.
         */
        builder.HasOne<ClinicalNote>()
            .WithMany()
            .HasForeignKey(e => e.ClinicalNoteId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(e => e.Diagnoses)
            .WithOne()
            .HasForeignKey(d => d.EncounterId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(e => e.Diagnoses).UsePropertyAccessMode(PropertyAccessMode.Field);

        /*
         * A line bills at least one unit and no more than a four-hour appointment's worth.
         *
         * The aggregate refuses both, and this CHECK makes it true against a bulk insert or
         * a hand-run script — the same belt ClinicalNotes and Goals wear.
         */
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_Encounters_UnitsAreBillable",
            $"[Units] >= 1 AND [Units] <= {Encounter.MaxUnits}"));

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_Encounters_MoneyIsNotNegative",
            "[ChargeAmount] >= 0 AND [AmountPaid] >= 0"));

        /*
         * Money that arrived has a date, and a date means money arrived.
         *
         * Either half alone is a row that cannot be reconciled: an amount with no date
         * cannot be matched against a bank statement, and a date with no amount says a
         * payment happened for nothing. PaymentStatus is not in the constraint on purpose —
         * a Waived row (4) carries neither, and is covered by the first branch.
         */
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_Encounters_PaymentsCarryADate",
            "([AmountPaid] = 0 AND [PaidAtUtc] IS NULL) "
            + "OR ([AmountPaid] > 0 AND [PaidAtUtc] IS NOT NULL)"));
    }
}

public sealed class EncounterDiagnosisConfiguration : IEntityTypeConfiguration<EncounterDiagnosis>
{
    public void Configure(EntityTypeBuilder<EncounterDiagnosis> builder)
    {
        builder.ToTable("EncounterDiagnoses");

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedOnAdd();
        builder.HasIndex(d => d.PublicId).IsUnique();

        builder.Property(d => d.ProviderId).IsRequired();
        builder.Property(d => d.EncounterId).IsRequired();
        builder.Property(d => d.Sequence).IsRequired();

        // varchar, for the same reason CptCode is: an ICD-10-CM code is ASCII.
        builder.Property(d => d.Code)
            .HasColumnType($"varchar({Encounter.MaxDiagnosisCodeLength})").IsRequired();

        builder.Property(d => d.RowVersion).IsRowVersion();

        /*
         * One code per position, and one position per code.
         *
         * Two rows claiming to be the primary diagnosis is not a tie any later reader can
         * break, and the same code listed twice is a coding error a payer rejects. The
         * aggregate refuses both; these make it true of the table.
         */
        builder.HasIndex(d => new { d.EncounterId, d.Sequence })
            .IsUnique()
            .HasDatabaseName("UX_EncounterDiagnoses_OnePerPosition");

        builder.HasIndex(d => new { d.EncounterId, d.Code })
            .IsUnique()
            .HasDatabaseName("UX_EncounterDiagnoses_OneRowPerCode");

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_EncounterDiagnoses_SequenceIsAPointer",
            $"[Sequence] >= 1 AND [Sequence] <= {Encounter.MaxDiagnoses}"));
    }
}
