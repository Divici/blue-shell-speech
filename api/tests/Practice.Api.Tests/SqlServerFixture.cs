using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

        await using var db = new PracticeDbContext(options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class UsesSqlServer : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sqlserver";
}

/// <summary>
/// Boots the real API against the containerised database.
/// </summary>
public sealed class PracticeApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sql"] = connectionString,
            }));

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseEnvironment("Development");

    public IServiceScope CreateScope() => Services.CreateScope();
}
