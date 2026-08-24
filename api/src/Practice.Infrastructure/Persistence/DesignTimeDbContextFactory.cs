using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Practice.Application.Providers;

namespace Practice.Infrastructure.Persistence;

/// <summary>
/// Builds a DbContext for `dotnet ef` at design time.
///
/// WHY THIS EXISTS: without it, `dotnet ef migrations add` boots the API's host to obtain
/// a DbContext — and the API deliberately refuses to start without a connection string.
/// That made migrations depend on appsettings.Development.json, which is GITIGNORED, so
/// scaffolding a migration worked only on a machine that happened to have that file. A
/// fresh clone could not generate one.
///
/// The connection string here is used ONLY to build the model. `migrations add` never
/// opens a connection; `database update` does, and that reads real configuration.
/// Overridable via BLUESHELL_MIGRATIONS_CONNECTION for running updates against a
/// specific database.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PracticeDbContext>
{
    private const string LocalDefault =
        "Server=localhost,1433;Database=BlueShell;User Id=sa;Password=LocalDev!Passw0rd;TrustServerCertificate=True";

    public PracticeDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("BLUESHELL_MIGRATIONS_CONNECTION")
            ?? LocalDefault;

        var options = new DbContextOptionsBuilder<PracticeDbContext>()
            .UseSqlServer(connectionString, sql =>
            {
                /*
                 * Retries, because the first migration run against Azure SQL almost always
                 * hits a paused database.
                 *
                 * The free offer auto-pauses on idle, and resuming takes tens of seconds —
                 * so the first connection fails as a "transient error" and the migration
                 * aborts. Without this, applying migrations to a fresh environment fails
                 * on the first attempt and succeeds on the second, which looks like
                 * flakiness rather than the documented behaviour it is.
                 */
                sql.EnableRetryOnFailure(maxRetryCount: 10, TimeSpan.FromSeconds(20), null);
                sql.CommandTimeout(180);
            })
            .Options;

        /*
         * A null provider context.
         *
         * Migrations describe the schema, not a tenant's view of it. Supplying a real
         * provider would bake a filter value into nothing useful — and null is the safe
         * value everywhere else in this system, so it is the safe value here too.
         */
        return new PracticeDbContext(options, new FixedProviderContext(null));
    }
}
