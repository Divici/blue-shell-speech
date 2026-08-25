using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Practice.Application.Providers;
using Practice.Domain.Auditing;
using Practice.Domain.Billing;
using Practice.Domain.ClinicalNotes;
using Practice.Domain.Consultations;
using Practice.Domain.Goals;
using Practice.Domain.Patients;
using Practice.Domain.Resources;
using Practice.Domain.Scheduling;
using Practice.Domain.Providers;
using Practice.Infrastructure.Identity;

namespace Practice.Infrastructure.Persistence;

/// <summary>
/// The system of record.
///
/// Identity's tables and the clinical tables share one database but not one concern —
/// see PracticeUser for why they are kept apart at the type level.
/// </summary>
public sealed class PracticeDbContext(
    DbContextOptions<PracticeDbContext> options,
    IProviderContext providerContext)
    : IdentityDbContext<PracticeUser>(options)
{
    public DbSet<Provider> Providers => Set<Provider>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<Patient> Patients => Set<Patient>();

    public DbSet<Guardian> Guardians => Set<Guardian>();

    public DbSet<PatientAddress> PatientAddresses => Set<PatientAddress>();

    public DbSet<Appointment> Appointments => Set<Appointment>();

    public DbSet<Goal> Goals => Set<Goal>();

    public DbSet<ClinicalNote> ClinicalNotes => Set<ClinicalNote>();

    public DbSet<ConsultationRequest> ConsultationRequests => Set<ConsultationRequest>();

    public DbSet<Encounter> Encounters => Set<Encounter>();

    public DbSet<EncounterDiagnosis> EncounterDiagnoses => Set<EncounterDiagnosis>();

    public DbSet<ResourceDocument> ResourceDocuments => Set<ResourceDocument>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(PracticeDbContext).Assembly);

        /*
         * TENANCY, ENFORCED BY DEFAULT.
         *
         * Every query against patient data is filtered by the current provider unless a
         * developer explicitly opts out with IgnoreQueryFilters(). That inversion is the
         * point: forgetting the filter is impossible, and bypassing it is a visible,
         * greppable act.
         *
         * A NULL provider matches NOTHING. Unauthenticated code paths therefore see an
         * empty database rather than every record — the safe direction to fail.
         *
         * This is a defence in depth, not the only control: the API re-checks ownership
         * on every read and write (docs/SECURITY.md). Hiding rows is not authorization.
         */
        builder.Entity<Patient>().HasQueryFilter(
            p => providerContext.ProviderId != null && p.ProviderId == providerContext.ProviderId);
        builder.Entity<Guardian>().HasQueryFilter(
            g => providerContext.ProviderId != null && g.ProviderId == providerContext.ProviderId);
        builder.Entity<PatientAddress>().HasQueryFilter(
            a => providerContext.ProviderId != null && a.ProviderId == providerContext.ProviderId);
        builder.Entity<Appointment>().HasQueryFilter(
            a => providerContext.ProviderId != null && a.ProviderId == providerContext.ProviderId);
        builder.Entity<Goal>().HasQueryFilter(
            g => providerContext.ProviderId != null && g.ProviderId == providerContext.ProviderId);
        builder.Entity<ClinicalNote>().HasQueryFilter(
            n => providerContext.ProviderId != null && n.ProviderId == providerContext.ProviderId);

        /*
         * Filtered like everything else, even though it is WRITTEN by an anonymous caller.
         *
         * A query filter constrains reads, never inserts, so the public form's POST is
         * unaffected — it stamps the provider the API resolved and saves. What the filter
         * governs is the other half: the enquiry is read back through a session, and until
         * one exists it is invisible to the same request that created it. That asymmetry
         * is the intended shape of a public intake row, not an oversight.
         */
        builder.Entity<ConsultationRequest>().HasQueryFilter(
            c => providerContext.ProviderId != null && c.ProviderId == providerContext.ProviderId);

        /*
         * Billing is patient data, and it is filtered like patient data.
         *
         * EncounterDiagnosis gets its OWN filter rather than relying on its parent's. A
         * child row protected only by the filter on the row above it is the defect D066 F4
         * found on ClinicalNote and D073 found twice more on Guardian and PatientAddress:
         * the parent covers for the child, so the child's filter can be deleted and no test
         * goes red. The test for this one plants a foreign diagnosis on an encounter the
         * caller genuinely owns, which is the only shape where this filter is the sole
         * thing in the way.
         */
        builder.Entity<Encounter>().HasQueryFilter(
            e => providerContext.ProviderId != null && e.ProviderId == providerContext.ProviderId);
        builder.Entity<EncounterDiagnosis>().HasQueryFilter(
            d => providerContext.ProviderId != null && d.ProviderId == providerContext.ProviderId);

        /*
         * Filtered too, even though the handouts are meant to be public.
         *
         * This is the same asymmetry ConsultationRequest has, pointing the other way. The
         * WRITE side is authenticated and belongs to a provider, so the management surface
         * must be scoped like every other table — otherwise a second clinician's unpublished
         * drafts appear in the first one's library. The READ side is anonymous, and the
         * public page has no session to arm the filter with.
         *
         * Resolved by keeping the filter ordinary and making the public read a deliberate,
         * greppable IgnoreQueryFilters().Where(r => r.IsPublished) when that page is built,
         * rather than teaching this filter a null-provider special case. A conditional
         * filter would be inherited by whatever gets added to this table next, and the safe
         * direction to fail (D051: null matches NOTHING) would stop being true everywhere.
         * Forgetting the opt-out renders an empty page, which is visible and leaks nothing.
         */
        builder.Entity<ResourceDocument>().HasQueryFilter(
            r => providerContext.ProviderId != null && r.ProviderId == providerContext.ProviderId);

        /*
         * Every DateTime is UTC, enforced at the mapping layer.
         *
         * SQL Server's datetime2 carries no offset, so a value read back has DateTimeKind
         * Unspecified. Left alone, that silently produces local-time arithmetic on a
         * machine in a different zone from the one that wrote it — and this app stores
         * UTC while rendering America/New_York, which is exactly the setup where such a
         * bug hides until a DST boundary.
         *
         * THIS PARAGRAPH USED TO BE A CLAIM RATHER THAN A CONTROL. The loop below set the
         * column type and nothing else, so nothing stamped the Kind and the comment
         * described a guarantee that did not exist. What it cost: System.Text.Json writes
         * an Unspecified DateTime with no Z, so every timestamp READ BACK from the
         * database reached the browser as a floating local time while the same field
         * echoed from an in-memory entity carried the designator. A 9am visit read as "not
         * started yet" until 1pm on the day view, and a signature time displayed four hours
         * out on a signed clinical note.
         *
         * The converter is READ-ONLY on purpose — `v => v` on the way in. Coercing a Local
         * value to UTC on write would quietly repair a caller that used DateTime.Now, and
         * this codebase asserts instead: Sign() and DocumentationBlockedReason both throw
         * on a non-UTC Kind. There is nothing to assert on the way out, because the
         * database genuinely has no Kind to give back — stamping it is restoring
         * information the column type cannot carry, which is a different act from
         * papering over a caller's mistake.
         */
        var utcConverter = new ValueConverter<DateTime, DateTime>(
            value => value,
            value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

        var nullableUtcConverter = new ValueConverter<DateTime?, DateTime?>(
            value => value,
            value => value.HasValue
                ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
                : null);

        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                {
                    property.SetColumnType("datetime2(3)");
                    property.SetValueConverter(utcConverter);
                }
                else if (property.ClrType == typeof(DateTime?))
                {
                    property.SetColumnType("datetime2(3)");
                    property.SetValueConverter(nullableUtcConverter);
                }
            }
        }
    }
}
