using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Practice.Domain.ClinicalNotes;
using Practice.Domain.Goals;

namespace Practice.Infrastructure.Persistence.Configurations;

public sealed class GoalConfiguration : IEntityTypeConfiguration<Goal>
{
    public void Configure(EntityTypeBuilder<Goal> builder)
    {
        builder.ToTable("Goals");

        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedOnAdd();
        builder.HasIndex(g => g.PublicId).IsUnique();

        builder.Property(g => g.ProviderId).IsRequired();
        builder.Property(g => g.PatientId).IsRequired();
        builder.Property(g => g.GoalText).HasMaxLength(1000).IsRequired();
        builder.Property(g => g.Domain).IsRequired();
        builder.Property(g => g.TargetCriteria).HasMaxLength(500);
        builder.Property(g => g.Status).IsRequired();
        builder.Property(g => g.StartDate).HasColumnType("date").IsRequired();
        builder.Property(g => g.EndDate).HasColumnType("date");
        builder.Property(g => g.AacDeviceNotes).HasMaxLength(500);
        builder.Property(g => g.RowVersion).IsRowVersion();

        builder.Ignore(g => g.IsCurrentlyTargeted);

        // The dictation pipeline's query: "what is this patient currently working on".
        builder.HasIndex(g => new { g.PatientId, g.Status });

        /*
         * AAC details belong only on an AAC goal.
         *
         * The aggregate rejects the combination, and this CHECK makes it true against a
         * bulk insert or a hand-run script. GoalDomain.Aac = 7.
         */
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_Goals_AacFieldsOnlyOnAacGoals",
            "([Domain] = 7) OR ([AacModality] IS NULL AND [AacDeviceNotes] IS NULL)"));
    }
}

public sealed class ClinicalNoteConfiguration : IEntityTypeConfiguration<ClinicalNote>
{
    public void Configure(EntityTypeBuilder<ClinicalNote> builder)
    {
        builder.ToTable("ClinicalNotes");

        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedOnAdd();
        builder.HasIndex(n => n.PublicId).IsUnique();

        builder.Property(n => n.ProviderId).IsRequired();
        builder.Property(n => n.PatientId).IsRequired();
        builder.Property(n => n.AppointmentId).IsRequired();
        builder.Property(n => n.VersionNumber).IsRequired();
        builder.Property(n => n.IsCurrent).IsRequired();
        builder.Property(n => n.Status).IsRequired();
        builder.Property(n => n.Origin).IsRequired();

        builder.Property(n => n.Subjective).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(n => n.Objective).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(n => n.Assessment).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(n => n.Plan).HasColumnType("nvarchar(max)").IsRequired();

        builder.Property(n => n.SignedBy).HasMaxLength(450);
        builder.Property(n => n.AmendmentReason).HasMaxLength(500);

        // SHA-256 is exactly 32 bytes. Fixed width, so a truncated hash is impossible.
        builder.Property(n => n.ContentHash).HasColumnType("binary(32)");

        builder.Property(n => n.RowVersion).IsRowVersion();

        // Computed from the four section columns. A column would be a second answer that
        // could disagree with them.
        builder.Ignore(n => n.CanBeDiscarded);

        builder.HasIndex(n => new { n.PatientId, n.AppointmentId });
        builder.HasIndex(n => n.SupersedesNoteId);

        /*
         * EXACTLY ONE current note per appointment.
         *
         * A filtered unique index, so a second "current" note is rejected by the database
         * rather than merely avoided by the aggregate. Without it, a concurrent amendment
         * could fork the history and leave two notes claiming to be the visit's record,
         * with no way to say which one the clinician stands behind.
         */
        builder.HasIndex(n => n.AppointmentId)
            .HasFilter("[IsCurrent] = 1")
            .IsUnique()
            .HasDatabaseName("UX_ClinicalNotes_OneCurrentPerAppointment");

        /*
         * An amendment must carry a reason.
         *
         * "Why was this record changed" is the first question anyone asks of an amended
         * clinical note — an auditor, a lawyer, a colleague. A CHECK guarantees the answer
         * exists rather than trusting every write path to supply it.
         */
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_ClinicalNotes_AmendmentsHaveAReason",
            "([SupersedesNoteId] IS NULL) OR ([AmendmentReason] IS NOT NULL)"));

        /*
         * A signed note must have a signature and a hash.
         *
         * NoteStatus: Draft = 1, Signed = 2, Amended = 3. Signed and Amended are both
         * post-signature states, so both require the attestation fields.
         */
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_ClinicalNotes_SignedNotesAreAttested",
            "([Status] = 1) OR ([SignedAtUtc] IS NOT NULL AND [SignedBy] IS NOT NULL AND [ContentHash] IS NOT NULL)"));

        /*
         * EF Core MUST be told this table has a trigger.
         *
         * By default EF uses an OUTPUT clause to read back generated values after a save.
         * SQL Server does not allow OUTPUT on a table with an AFTER trigger, so without
         * this declaration every insert or update to ClinicalNotes fails at runtime with
         * an error that names neither the trigger nor the OUTPUT clause.
         *
         * Declaring it makes EF fall back to a SELECT after the write. Slightly slower,
         * and the only way to have both a trigger and EF on the same table.
         */
        builder.ToTable(t => t.HasTrigger("TR_ClinicalNotes_PreventSignedEdits"));
        builder.ToTable(t => t.HasTrigger("TR_ClinicalNotes_PreventDeletingRealNotes"));
    }
}
