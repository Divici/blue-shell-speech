using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Practice.Domain.Auditing;
using Practice.Domain.Providers;
using Practice.Infrastructure.Identity;

namespace Practice.Infrastructure.Persistence;

/// <summary>
/// The system of record.
///
/// Identity's tables and the clinical tables share one database but not one concern —
/// see PracticeUser for why they are kept apart at the type level.
/// </summary>
public sealed class PracticeDbContext(DbContextOptions<PracticeDbContext> options)
    : IdentityDbContext<PracticeUser>(options)
{
    public DbSet<Provider> Providers => Set<Provider>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(PracticeDbContext).Assembly);

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
