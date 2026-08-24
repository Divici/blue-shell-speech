using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Practice.Application.Providers;
using Practice.Domain.Auditing;
using Practice.Domain.Patients;
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

        /*
         * Every DateTime is UTC, enforced at the mapping layer.
         *
         * SQL Server's datetime2 carries no offset, so a value read back has DateTimeKind
         * Unspecified. Left alone, that silently produces local-time arithmetic on a
         * machine in a different zone from the one that wrote it — and this app stores
         * UTC while rendering America/New_York, which is exactly the setup where such a
         * bug hides until a DST boundary.
         */
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                {
                    property.SetColumnType("datetime2(3)");
                }
            }
        }
    }
}
