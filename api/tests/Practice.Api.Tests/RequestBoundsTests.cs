using System.Net;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Practice.Api.Auth;
using Practice.Domain.Providers;
using Practice.Infrastructure.Identity;
using Practice.Infrastructure.Persistence;

namespace Practice.Api.Tests;

/// <summary>
/// How long a request is allowed to hold a connection when nobody is waiting for it.
///
/// Neither bound existed. <c>AddInfrastructure</c> set no command timeout — while
/// DesignTimeDbContextFactory sets 180 twenty lines away — and nothing set a request
/// timeout, so a refusal issued against a database resuming from auto-pause could hold a
/// request and a pooled connection for minutes after the caller had gone. On a container
/// that scales to zero, connections are the resource that runs out first.
///
/// Both are asserted here rather than trusted, because a timeout is the kind of setting
/// that reads as present when it is absent: nothing fails, and the difference only shows
/// up on the day the database is slow.
/// </summary>
[Collection(UsesSqlServer.Name)]
public sealed class RequestBoundsTests(SqlServerFixture sql) : IDisposable
{
    private readonly PracticeApiFactory _factory = new(sql.ConnectionString);

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// The command timeout the application actually runs with.
    ///
    /// Read off a resolved DbContext rather than off the constant, so that configuring it
    /// and declaring it are two different facts and the test can tell them apart. A
    /// constant asserted against itself is the shape of test D042 finding #2 was.
    ///
    /// Control: the <c>sql.CommandTimeout(...)</c> call in
    /// InfrastructureServices.AddInfrastructure.
    /// Deleted → red, "Assert.Equal() Failure: Values differ, Expected: 30, Actual: null"
    /// — EF reports no configured timeout, and the bound falls back to whatever SqlClient
    /// or the connection string decides.
    /// </summary>
    [Fact]
    public void Every_database_command_is_bounded()
    {
        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        Assert.Equal(DatabaseTimeouts.CommandSeconds, db.Database.GetCommandTimeout());
    }

    /// <summary>
    /// The request timeout is REGISTERED — half the claim, and the weaker half.
    ///
    /// Options with no middleware to read them is precisely the D072 defect: configuration
    /// present, control absent, and everything looking correct to whoever greps for it.
    /// The test below is the half that matters; this one exists so that a failure can be
    /// attributed to the right half.
    ///
    /// Control: the <c>DefaultPolicy</c> assignment inside <c>AddRequestTimeouts</c> in
    /// Program.cs.
    /// Deleted — <c>AddRequestTimeouts()</c> left in place with no configuration — → red,
    /// "Assert.Equal() Failure: Values differ, Expected: 00:00:30, Actual: null".
    ///
    /// The POLICY is the control, not the registration: deleting <c>AddRequestTimeouts</c>
    /// outright takes the whole application down — "Unable to resolve service for type
    /// 'ICancellationTokenLinker' while attempting to activate
    /// 'RequestTimeoutsMiddleware'" — which fails every test in this class and isolates
    /// nothing.
    /// </summary>
    [Fact]
    public void A_default_request_timeout_is_configured()
    {
        using var scope = _factory.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<RequestTimeoutOptions>>();

        Assert.Equal(DatabaseTimeouts.Request, options.Value.DefaultPolicy?.Timeout);
    }

    /// <summary>
    /// The request timeout is APPLIED, on the pipeline the application actually runs.
    ///
    /// Forced with a policy of a few hundred milliseconds and an interceptor that makes
    /// every read take longer than that — the same shape as a database resuming from
    /// auto-pause, minus the wait. A request that outlives its caller must stop, and the
    /// honest answer to "this took longer than we are prepared to wait" is 504, not a
    /// response that never arrives.
    ///
    /// Any authenticated path would do: ProviderContextMiddleware resolves the forwarded
    /// provider with a query, so the delay lands before the endpoint is even chosen. That
    /// is the point — the bound is on the request, not on one route somebody remembered.
    ///
    /// Control: the <c>app.UseRequestTimeouts()</c> call in Program.cs.
    /// Deleted → red, "Assert.Equal() Failure: Values differ, Expected: GatewayTimeout,
    /// Actual: NotFound", after four seconds — the request runs to completion and answers
    /// normally, which is exactly the failure: nothing stopped it.
    /// </summary>
    [Fact]
    public async Task A_request_that_outlives_its_caller_is_stopped()
    {
        var providerPublicId = await SeedProviderAsync();

        var impatient = TimeSpan.FromMilliseconds(250);

        using var slow = new PracticeApiFactory(sql.ConnectionString, services =>
        {
            FailureHarness.With(
                sql.ConnectionString, new DelaysEveryRead(impatient * 8))(services);

            services.Configure<RequestTimeoutOptions>(
                options => options.DefaultPolicy = new RequestTimeoutPolicy { Timeout = impatient });
        });

        using var client = slow.CreateClient();
        client.DefaultRequestHeaders.Add(
            RequestProviderContext.HeaderName, providerPublicId.ToString());

        using var response = await client.GetAsync($"/notes/appointment/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    /// <summary>A provider, so the request under test is an authenticated one.</summary>
    private async Task<Guid> SeedProviderAsync()
    {
        using var scope = _factory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PracticeUser>>();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var email = $"bounds-{Guid.NewGuid():N}@example.com";
        var user = new PracticeUser { UserName = email, Email = email };
        await users.CreateAsync(user, "correct-horse-battery-staple");

        var provider = Provider.Create(user.Id, "Michelle", "M.S., CCC-SLP", "SLP-1", "MD");
        db.Providers.Add(provider);
        await db.SaveChangesAsync();

        return provider.PublicId;
    }
}
