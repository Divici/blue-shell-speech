using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Practice.Application.Providers;
using Practice.Infrastructure.Persistence;
using Testcontainers.MsSql;

namespace Practice.Api.Tests;

/// <summary>
/// A real SQL Server, in a container, per test run.
///
/// NOT the EF Core InMemory provider, and not SQLite (docs/TEST_STRATEGY.md, D020). Every
/// mechanism protecting the clinical record is provider-specific — the trigger blocking
/// edits to signed notes, filtered unique indexes, rowversion concurrency, datetime2
/// precision — and an in-memory provider fakes all of it away. A suite that passes against
/// a different engine than production proves the wrong thing, and the things it fails to
/// prove are exactly the ones that matter.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Migrations, not EnsureCreated: this exercises the same scripts production runs,
        // so a broken migration fails here rather than at deploy time.
        var options = new DbContextOptionsBuilder<PracticeDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        // Migrations only — no provider context needed, and none available before any
        // provider exists.
        await using var db = new PracticeDbContext(options, new FixedProviderContext(null));
        await db.Database.MigrateAsync();
    }

    /// <summary>
    /// A migrated database of its own, in the SAME container.
    ///
    /// WHY ANY TEST WOULD WANT ONE. Every class in the sqlserver collection shares one
    /// database, and each of them seeds providers as it goes — so by the time any given
    /// class runs, the Providers table holds however many its predecessors created. That
    /// is harmless for endpoints that take a provider from a header, and fatal for
    /// <c>POST /consultation-requests</c>, whose whole design is that it resolves THE SOLE
    /// ACTIVE PROVIDER and refuses when the answer is ambiguous. Asserting that rule needs
    /// a table whose contents the test controls.
    ///
    /// A second database rather than a second container: SQL Server takes tens of seconds
    /// to start and the collection already pays that once. A second container would also
    /// hide a real property of the deployment — one server, one engine, one set of
    /// triggers — behind a difference that does not exist in production.
    ///
    /// Deliberately NOT a way to make tests independent in general. The shared database is
    /// the honest default: rows other tests left behind are exactly the conditions the
    /// application runs in, and a test that only passes in an empty schema is a test that
    /// has not met production.
    /// </summary>
    public async Task<string> CreateIsolatedDatabaseAsync(string name)
    {
        // Sanitised rather than trusted: the name is interpolated into DDL, which cannot
        // be parameterised. Test-only, and still not a place to be relaxed about it.
        if (!name.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("Letters and digits only.", nameof(name));
        }

        var builder = new SqlConnectionStringBuilder(ConnectionString);

        await using (var connection = new SqlConnection(builder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText = $"IF DB_ID(N'{name}') IS NULL CREATE DATABASE [{name}];";
            await create.ExecuteNonQueryAsync();
        }

        builder.InitialCatalog = name;

        var options = new DbContextOptionsBuilder<PracticeDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .Options;

        // Migrations, for the same reason InitializeAsync uses them: this exercises the
        // scripts production runs.
        await using var db = new PracticeDbContext(options, new FixedProviderContext(null));
        await db.Database.MigrateAsync();

        return builder.ConnectionString;
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>
/// One collection, two containers.
///
/// <see cref="AzuriteFixture"/> joins <see cref="SqlServerFixture"/> here rather than being
/// started per test class, because the readiness probe reads blob storage and two classes
/// need it. A collection fixture starts it once for the run; a class fixture would start it
/// once per class, and a second emulator would cost more than the tests that use it.
/// </summary>
[CollectionDefinition(Name)]
public sealed class UsesSqlServer
    : ICollectionFixture<SqlServerFixture>, ICollectionFixture<AzuriteFixture>
{
    public const string Name = "sqlserver";
}

/// <summary>
/// Boots the real API against the containerised database.
///
/// <paramref name="configureServices"/> replaces registrations AFTER the application has
/// built its own, and exists for one purpose: making a dependency fail on demand. Some
/// guarantees are only observable when something downstream breaks — "the audit row and
/// the delete commit together" cannot be proven by a run where both succeed. Every test
/// that uses it says which failure it is forcing and why.
/// </summary>
public sealed class PracticeApiFactory(
    string connectionString,
    Action<IServiceCollection>? configureServices = null,
    string? storageConnectionString = null) : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        /*
         * ConnectionStrings:Storage is OPTIONAL and left null by most callers, on purpose.
         *
         * The blob readiness probe reports unready when it is absent, which is the right
         * answer and is asserted in ReadinessCheckTests — but only the health tests read
         * /health/ready, so paying for storage configuration in every other class would buy
         * nothing. Whoever needs a reachable account passes AzuriteFixture.ConnectionString.
         */
        builder.ConfigureHostConfiguration(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sql"] = connectionString,
                ["ConnectionStrings:Storage"] = storageConnectionString,
            }));

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        if (configureServices is not null)
        {
            builder.ConfigureTestServices(configureServices);
        }
    }

    public IServiceScope CreateScope() => Services.CreateScope();
}
