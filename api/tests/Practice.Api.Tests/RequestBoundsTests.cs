using System.Net;
using System.Globalization;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Practice.Api.Auth;
using Practice.Api.ClinicalNotes;
using Practice.Api.Patients;
using Practice.Api.Scheduling;
using Practice.Domain.Providers;
using Practice.Domain.Scheduling;
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
    /// "Assert.Equal() Failure: Values differ, Expected: 00:04:20, Actual: null". Re-run
    /// after the bound stopped being a chosen number and became one derived from the retry
    /// budget (D086); the quoted value moved with it, which is the sort of drift a
    /// `Control:` line is meant to make visible.
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

    // ------------------------------------------ the two bounds against each other (1.15 F1)

    /*
     * A REQUEST TIMEOUT UNDER THE RETRY BUDGET CANCELS THE RETRIES THAT EXIST TO SAVE IT.
     *
     * EnableRetryOnFailure is in AddInfrastructure for one stated reason: Azure SQL
     * serverless auto-pauses, and the first query after a pause fails while the database
     * resumes. Michelle's first request of the day IS that query. A request timeout of
     * thirty seconds sitting on top of a retry policy allowed six commands and fifty
     * seconds of backoff cancels the wake-up it was configured to survive, and answers 504
     * on the one request a day that was always going to be slow.
     *
     * The two numbers are set in different files, in different assemblies, for different
     * reasons. Nothing brings them into contact except this test.
     */

    /// <summary>
    /// The request bound is longer than the longest run the retry policy can produce.
    ///
    /// Every term is read off the RUNNING application — the command timeout from the
    /// resolved context, the retry count and the maximum backoff from the execution
    /// strategy the context builds, the request bound from the registered policy — so this
    /// asserts a relationship between three live settings rather than restating a constant
    /// (D042 finding #2, and the reason <see cref="Every_database_command_is_bounded"/>
    /// reads the context instead of DatabaseTimeouts).
    ///
    /// Control: <c>DatabaseTimeouts.Request</c> being derived from
    /// <c>RequestTimeoutFor</c> rather than chosen.
    /// Restored to the flat <c>TimeSpan.FromSeconds(30)</c> this replaces → red, "The
    /// request bound is 00:00:30, and the retry policy this application configures can keep
    /// one command running for 00:03:50 (6 attempts x 00:00:30, plus 5 backoffs of up to
    /// 00:00:10). A request timeout below that cancels the retries that exist to carry
    /// Michelle's first request of the day through an auto-paused database."
    ///
    /// Also red on the narrower deletion — the <c>maxRetryDelay * maxRetryCount</c> term
    /// inside <c>RetryBudgetFor</c>'s use by <c>RequestTimeoutFor</c> — with the same
    /// sentence and "The request bound is 00:03:30". Two ways to break one relationship,
    /// and the test names the relationship rather than either term.
    /// </summary>
    [Fact]
    public void The_request_bound_outlives_the_retry_budget()
    {
        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<RequestTimeoutOptions>>();

        var command = TimeSpan.FromSeconds(
            db.Database.GetCommandTimeout()
            ?? throw new InvalidOperationException("No command timeout is configured."));

        var (retries, backoff) = RetryPolicyOf(db.Database.CreateExecutionStrategy());
        var request = options.Value.DefaultPolicy?.Timeout
            ?? throw new InvalidOperationException("No default request timeout is configured.");

        var budget = DatabaseTimeouts.RetryBudgetFor(command, retries, backoff);

        Assert.True(
            request > budget,
            $"The request bound is {request}, and the retry policy this application "
            + $"configures can keep one command running for {budget} ({retries + 1} attempts "
            + $"x {command}, plus {retries} backoffs of up to {backoff}). A request timeout "
            + "below that cancels the retries that exist to carry Michelle's first request "
            + "of the day through an auto-paused database.");
    }

    /// <summary>
    /// The relationship above, exercised rather than computed.
    ///
    /// Scaled down so it costs a second: a strategy that retries once after a fixed,
    /// measurable backoff, and a request timeout derived by the SAME function from those
    /// scaled numbers. The request spends longer in the retry loop than any single command
    /// is allowed to take, which is precisely the shape of a database resuming from
    /// auto-pause — and it must still arrive.
    ///
    /// The arithmetic is the thing under test, so the policy comes from
    /// <c>DatabaseTimeouts.RequestTimeoutFor</c> rather than from a number chosen here. A
    /// test that picked its own comfortable timeout would pass whatever the function did.
    ///
    /// Control: the <c>maxRetryDelay * maxRetryCount</c> term
    /// <c>DatabaseTimeouts.RequestTimeoutFor</c> takes from <c>RetryBudgetFor</c>.
    /// Deleted → red, "Assert.Equal() Failure: Values differ, Expected: OK, Actual:
    /// GatewayTimeout" — without the backoff term the derived bound is shorter than the
    /// wait the retry policy is in the middle of, and the middleware kills the request the
    /// retry was about to rescue.
    /// </summary>
    [Fact]
    public async Task A_request_the_retry_policy_is_carrying_is_not_cut_off()
    {
        var providerPublicId = await SeedProviderAsync();

        using var client = ClientFor(_factory, providerPublicId);
        var draft = await SeedEmptyDraftAsync(client);

        // One command's worth of patience, one retry, and a backoff long enough that a
        // bound which ignored it would fire in the middle of the wait.
        var command = TimeSpan.FromMilliseconds(250);
        var backoff = TimeSpan.FromMilliseconds(1200);
        const int Retries = 1;

        using var resuming = new PracticeApiFactory(sql.ConnectionString, services =>
        {
            FailureHarness.RetriesAfterAMeasurableWait(
                sql.ConnectionString, services, Retries, backoff);

            services.Configure<RequestTimeoutOptions>(options => options.DefaultPolicy =
                new RequestTimeoutPolicy
                {
                    Timeout = DatabaseTimeouts.RequestTimeoutFor(command, Retries, backoff),
                });
        });

        using var resumingClient = ClientFor(resuming, providerPublicId);

        // The discard is the one write in this API that goes through the execution
        // strategy AND writes an audit row, so the blipping writer forces exactly one
        // retry of the whole transaction body.
        using var response = await resumingClient.DeleteAsync($"/notes/{draft}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The BFF gives up AFTER this API does, not before — checked across the tree boundary.
    ///
    /// This test exists because the comment it replaces did not. DatabaseTimeouts justified
    /// a thirty-second request bound with "the BFF gives up at twenty-five
    /// (<c>web/lib/api</c>)", and <c>AbortSignal.timeout</c> appeared exactly once in the
    /// whole web tree — on the public consultation form. Five of the six clients, every one
    /// of them the clinician's, set no signal at all. A claim about another language's
    /// constant, in a comment, with nothing able to notice it going stale: D072's class,
    /// fourth appearance.
    ///
    /// So the number is read out of the file rather than described. Two trees, one
    /// relationship, and a test that fails when either side moves.
    ///
    /// Control: <c>API_TIMEOUT_MS</c> in web/lib/api/timeouts.ts.
    /// Lowered to the 25_000 the old comment claimed → red, "The BFF gives up after
    /// 00:00:25 while this API is prepared to spend 00:04:20 on a request. The tier that
    /// gives up first decides the bound, so a shorter BFF timeout silently replaces every
    /// number on DatabaseTimeouts — including the retry budget it is sized around."
    /// </summary>
    [Fact]
    public void The_bff_waits_longer_than_this_api_is_prepared_to_spend()
    {
        var source = File.ReadAllText(RepoFile("web/lib/api/timeouts.ts"));

        var declared = Regex.Match(
            source, @"API_TIMEOUT_MS\s*=\s*([0-9_]+)", RegexOptions.None, TimeSpan.FromSeconds(1));

        Assert.True(declared.Success,
            "web/lib/api/timeouts.ts no longer declares API_TIMEOUT_MS. The two tiers' "
            + "timeouts are related and nothing else relates them.");

        var bff = TimeSpan.FromMilliseconds(
            int.Parse(
                declared.Groups[1].Value.Replace("_", "", StringComparison.Ordinal),
                CultureInfo.InvariantCulture));

        Assert.True(
            bff > DatabaseTimeouts.Request,
            $"The BFF gives up after {bff} while this API is prepared to spend "
            + $"{DatabaseTimeouts.Request} on a request. The tier that gives up first "
            + "decides the bound, so a shorter BFF timeout silently replaces every number "
            + "on DatabaseTimeouts — including the retry budget it is sized around.");
    }

    /// <summary>
    /// The repository root, found by walking up from the test assembly.
    ///
    /// The build output sits several directories below the tree, and the depth differs
    /// between a local run and CI. Walking up to the file being asserted on is the version
    /// that does not encode either.
    /// </summary>
    private static string RepoFile(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
             dir is not null;
             dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            $"Could not find {relativePath} above {AppContext.BaseDirectory}.");
    }

    /// <summary>
    /// The retry count and maximum backoff the CONFIGURED execution strategy holds.
    ///
    /// Both live on <see cref="ExecutionStrategy"/> rather than on
    /// <see cref="IExecutionStrategy"/>, so a DbContext cannot be asked what its retry
    /// policy allows through the interface EF hands out. Reflection is the price of reading
    /// the configured value instead of restating the literal, which is the whole point of
    /// this class. A rename throws with the type's name in the message rather than passing
    /// quietly.
    /// </summary>
    private static (int Retries, TimeSpan Backoff) RetryPolicyOf(IExecutionStrategy strategy)
    {
        var retries = Protected(strategy, "MaxRetryCount");
        var backoff = Protected(strategy, "MaxRetryDelay");

        return ((int)retries, (TimeSpan)backoff);
    }

    /// <summary>
    /// One instance property, looked up down the whole inheritance chain.
    ///
    /// <c>Type.GetProperty</c> searches the type it is asked about and its PUBLIC
    /// inheritance, which is not enough to be sure of finding a member EF may make
    /// protected again: these are declared on <see cref="ExecutionStrategy"/> while the
    /// object is a <c>SqlServerRetryingExecutionStrategy</c>. Walking the chain with
    /// <c>DeclaredOnly</c> finds it either way, and throws by name if EF removes it.
    /// </summary>
    private static object Protected(object instance, string name)
    {
        const BindingFlags Declared = BindingFlags.Instance
            | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;

        for (var type = instance.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperty(name, Declared);
            if (property is not null) return property.GetValue(instance)!;
        }

        throw new InvalidOperationException(
            $"{instance.GetType().Name} no longer exposes {name}. The retry budget has to be "
            + "read from the configured execution strategy, not restated in this test.");
    }

    /// <summary>A client carrying the forwarded provider identity.</summary>
    private static HttpClient ClientFor(PracticeApiFactory factory, Guid providerPublicId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            RequestProviderContext.HeaderName, providerPublicId.ToString());
        return client;
    }

    /// <summary>
    /// A visit and an empty draft on it — the one row this API will delete.
    ///
    /// Seeded through the ordinary endpoints so the row is exactly what the product
    /// produces, and through a DIFFERENT factory from the one under test so none of this
    /// setup runs against the blipping writer that forces the retry.
    /// </summary>
    private static async Task<Guid> SeedEmptyDraftAsync(HttpClient client)
    {
        using var patientResponse = await client.PostAsJsonAsync("/patients",
            new CreatePatientRequest("Ari", "Nakamura", new DateOnly(2023, 3, 14), null));
        var patient = (await patientResponse.Content.ReadFromJsonAsync<PatientDetail>())!;

        using var visitResponse = await client.PostAsJsonAsync("/appointments",
            new ScheduleAppointmentRequest(
                patient.PublicId, AppointmentType.Therapy,
                DateTime.UtcNow.Date.AddDays(-7).AddHours(9), 60, null, null));
        visitResponse.EnsureSuccessStatusCode();

        var visit = (await visitResponse.Content.ReadFromJsonAsync<ScheduledVisit>())!.PublicId;

        using var noteResponse = await client.PostAsJsonAsync("/notes",
            new CreateNoteRequest(visit, "", "", "", ""));
        noteResponse.EnsureSuccessStatusCode();

        return (await noteResponse.Content.ReadFromJsonAsync<NoteDto>())!.PublicId;
    }

    /// <summary>Just the id, from the scheduling endpoint's anonymous response.</summary>
    private sealed record ScheduledVisit(Guid PublicId);

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
