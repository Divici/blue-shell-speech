using System.Diagnostics;
using System.Net;
using System.Globalization;
using System.Net.Http.Json;
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
    /// "Assert.Equal() Failure: Values differ, Expected: 00:10:20, Actual: null". Re-run
    /// twice now: once when the bound stopped being a chosen number and became one derived
    /// from the retry budget (D086), and again when that budget stopped modelling one
    /// command per attempt (D090). The quoted value moved both times — 00:00:30, then
    /// 00:04:20, now 00:10:20 — which is exactly the drift a `Control:` line is meant to
    /// make visible, and the reason D077 makes re-running it part of the change.
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
    /// ONE ATTEMPT OF THE DISCARD ISSUES THIS MANY COMMANDS — counted, on a real DELETE,
    /// against a real database.
    ///
    /// This is the term two rounds of this fix got wrong. <c>RetryBudgetFor</c> multiplied
    /// a command timeout by the number of ATTEMPTS and stopped there, which models one
    /// command per attempt; the discard's transaction body issues a SELECT, a DELETE and
    /// an audit INSERT, so the budget was short by a factor of three and the request bound
    /// derived from it cancelled retries it claimed to contain. Nothing went red, because
    /// the test guarding it compared two constants — and two constants agree with each
    /// other whatever the system does.
    ///
    /// So the number is measured rather than read off the code. The interceptors count
    /// what EF executes between the transaction opening and it closing, which is exactly
    /// the unit the execution strategy retries; a body that grows a fourth statement makes
    /// this fail rather than making the budget quietly wrong.
    ///
    /// <c>Assert.Single</c> on the tally is a second claim worth having: the discard opens
    /// ONE transaction. Two would mean it had stopped being atomic with nothing else
    /// noticing.
    ///
    /// Control: <c>DatabaseTimeouts.DiscardCommandsPerAttempt</c>, the value 3.
    /// Set to 1 — the model this replaces — → red, "One attempt of the discard's
    /// transaction executed 3 command(s); DatabaseTimeouts.DiscardCommandsPerAttempt says
    /// 1. The retry budget multiplies a command timeout by this number, so a model that is
    /// short by a factor makes the request bound cancel retries it claims to contain."
    /// </summary>
    [Fact]
    public async Task The_discard_issues_the_commands_the_budget_models()
    {
        var providerPublicId = await SeedProviderAsync();

        var tally = new CommandsPerTransaction();

        using var counted = new PracticeApiFactory(sql.ConnectionString,
            FailureHarness.With(
                sql.ConnectionString,
                new CountsCommandsInATransaction(tally),
                new MarksTransactionBoundaries(tally)));

        using var client = ClientFor(counted, providerPublicId);

        // Seeded through the SAME factory — the counter is disarmed until the line below,
        // so none of this setup is counted, and arming after it means the two reads and
        // the write that are counted are the ones the DELETE performs.
        var draft = await SeedEmptyDraftAsync(client);

        tally.Arm();

        using var response = await client.DeleteAsync($"/notes/{draft}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var inTheTransaction = Assert.Single(tally.Counts);

        Assert.Equal(DatabaseTimeouts.DiscardCommandsPerAttempt, inTheTransaction);
    }

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
    /// The fourth term, the commands one attempt issues, cannot be read off a running
    /// application: it is a property of the endpoint's body rather than of a setting. It
    /// is measured instead, by
    /// <see cref="The_discard_issues_the_commands_the_budget_models"/>, and this test uses
    /// the constant that test pins.
    ///
    /// THE BUDGET IS COMPUTED HERE RATHER THAN BY CALLING <c>RetryBudgetFor</c>, which is
    /// the difference between a test and a tautology. Expressed through that function,
    /// both sides of the comparison move together whenever a term inside it changes — the
    /// policy is derived from it too — so deleting the very factor this round is about
    /// would leave this green. Written out, the function is on one side and the arithmetic
    /// it is supposed to implement is on the other.
    ///
    /// Control: <c>DatabaseTimeouts.Request</c> being derived from
    /// <c>RequestTimeoutFor</c> rather than chosen.
    /// Restored to the flat <c>TimeSpan.FromSeconds(30)</c> two rounds ago → red, "The
    /// request bound is 00:00:30, and the retry policy this application configures can keep
    /// one operation running for 00:09:50 (6 attempts x 3 commands x 00:00:30, plus 5
    /// backoffs of up to 00:00:10). A request timeout below that cancels the retries that
    /// exist to carry Michelle's first request of the day through an auto-paused database."
    ///
    /// Control: the <c>commandsPerAttempt</c> factor inside <c>RetryBudgetFor</c> — this
    /// round's term.
    /// Deleted → red, same sentence, "The request bound is 00:04:20" against the same
    /// 00:09:50 budget.
    ///
    /// Control: the <c>maxRetryDelay * maxRetryCount</c> term inside <c>RetryBudgetFor</c>.
    /// Deleted → red, same sentence, "The request bound is 00:09:30". Three ways to break
    /// one relationship, and the test names the relationship rather than any one term.
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

        const int Commands = DatabaseTimeouts.DiscardCommandsPerAttempt;
        var budget = (command * Commands * (retries + 1)) + (backoff * retries);

        Assert.True(
            request > budget,
            $"The request bound is {request}, and the retry policy this application "
            + $"configures can keep one operation running for {budget} ({retries + 1} "
            + $"attempts x {Commands} commands x {command}, plus {retries} backoffs of up "
            + $"to {backoff}). A request timeout below that cancels the retries that exist "
            + "to carry Michelle's first request of the day through an auto-paused "
            + "database.");
    }

    /// <summary>
    /// The relationship above, exercised rather than computed — INCLUDING THE TERM THAT
    /// WAS WRONG.
    ///
    /// The previous version of this test could not fail for the reason it existed. It
    /// derived its request bound from <c>command: 250ms</c> while the harness pinned the
    /// running application's command timeout at production's thirty seconds, so the
    /// request never spent measurable time in a command at all: every second of the wait
    /// was backoff, and the command term could have been any value without changing the
    /// outcome. "One command's worth of patience" was a sentence about a number nothing
    /// used.
    ///
    /// So the harness takes the command timeout now, and the request is made to spend real
    /// time in COMMANDS as well as in backoff — an interceptor stalls every read, which is
    /// the shape of a database that has accepted a statement and is still thinking about
    /// it. The scaled numbers are picked so the run sits between the bound this function
    /// derives and the bound it would derive with either term missing, which is the only
    /// arrangement in which both terms are load-bearing:
    ///
    ///   seven stalled reads x 800ms + one 3s backoff  = 8.6s of real work
    ///   the derived bound, 7 x 1s + 3s                = 10.0s  (1.4s of headroom)
    ///   without the backoff term, 7 x 1s              =  7.0s  (would cut it off)
    ///   without the commands factor, 3 x 1s + 3s      =  6.0s  (would cut it off)
    ///
    /// Control: the <c>commandsPerAttempt</c> factor in
    /// <c>DatabaseTimeouts.RetryBudgetFor</c> — this round's term.
    /// Deleted (the budget reverted to one command per attempt) → red, "Assert.Equal()
    /// Failure: Values differ, Expected: OK, Actual: GatewayTimeout" after 6 seconds.
    ///
    /// Also red on the <c>maxRetryDelay * maxRetryCount</c> term, same message, after 7
    /// seconds — the deletion the previous version of this line named, re-run against the
    /// rewritten test (D077).
    /// </summary>
    [Fact]
    public async Task A_request_the_retry_policy_is_carrying_is_not_cut_off()
    {
        var providerPublicId = await SeedProviderAsync();

        using var client = ClientFor(_factory, providerPublicId);
        var draft = await SeedEmptyDraftAsync(client);

        // Whole seconds: sql.CommandTimeout takes an int, so a fractional value here would
        // be a number the running application does not have — which is the defect this
        // rewrite is about.
        var command = TimeSpan.FromSeconds(1);
        var backoff = TimeSpan.FromSeconds(3);
        var stall = TimeSpan.FromMilliseconds(800);
        const int Retries = 1;

        using var resuming = new PracticeApiFactory(sql.ConnectionString, services =>
        {
            FailureHarness.RetriesAfterAMeasurableWait(
                sql.ConnectionString, services, Retries, backoff, command,
                new DelaysEveryRead(stall));

            services.Configure<RequestTimeoutOptions>(options => options.DefaultPolicy =
                new RequestTimeoutPolicy
                {
                    Timeout = DatabaseTimeouts.RequestTimeoutFor(
                        command, Retries, backoff, DatabaseTimeouts.DiscardCommandsPerAttempt),
                });
        });

        using var resumingClient = ClientFor(resuming, providerPublicId);

        // The discard is the one write in this API that goes through the execution
        // strategy AND writes an audit row, so the blipping writer forces exactly one
        // retry of the whole transaction body.
        using var response = await resumingClient.DeleteAsync($"/notes/{draft}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ------------------------------- the bound the request bound cannot set (1.16 F2)

    /*
     * A REQUEST TIMEOUT IS NOT A CEILING ON A REQUEST, AND TWO ROUNDS OF THIS FIX ASSUMED
     * IT WAS.
     *
     * RequestTimeoutsMiddleware cancels HttpContext.RequestAborted and then AWAITS the
     * rest of the pipeline. It stops work that observes a token. Audit writes deliberately
     * observe none (D075) — an audit row that vanishes when a phone locks is not an audit
     * row — so a DELETE against a wedged database ran its refusal audit ON PAST the
     * request bound, and the tier's real ceiling was the two ADDED rather than the larger
     * of the two. The nesting the repository had written down was false by its own
     * arithmetic: 260 + 230 against a BFF that gave up at 300.
     *
     * A bigger constant does not fix that, because the tail is precisely the part the
     * request bound cannot see. What fixes it is a second bound owned by the writes
     * themselves, and what proves it is a clock.
     */

    /// <summary>
    /// THE CEILING, MEASURED: the request bound, plus one grace period, and nothing else.
    ///
    /// The audit table is made to hang for twenty seconds; every other read answers
    /// normally, so the request reaches its refusal audit quickly and is still inside it
    /// when the request bound fires. Both halves of the claim are in the elapsed time:
    ///
    ///   * it is LONGER than the request bound, so there really is uncancellable work
    ///     running past that bound — a test that did not check this would pass on a system
    ///     where the tail did not exist and prove nothing about one where it does;
    ///   * it is SHORTER than the request bound plus the grace, which is the ceiling
    ///     <c>DatabaseTimeouts.Ceiling</c> claims and the number the BFF sits above.
    ///
    /// Scaled — 2s and 2s against production's 620 and 90 — because the relationship is
    /// what is under test and nothing can be measured against eleven minutes. The
    /// construction backstop is deliberately far away (60s), so that the deadline arriving
    /// on time is attributable to the binding rather than to the fallback.
    ///
    /// Control: <c>AuditWriter</c> saving on <c>deadline.Token</c>.
    /// Restored to <c>CancellationToken.None</c>, the shape before this commit → red,
    /// "The audit write ran for 20.1s past a 2s request bound. RequestTimeoutsMiddleware
    /// cancels RequestAborted and then waits, so an uncancellable write ADDS to the
    /// request bound rather than nesting inside it."
    ///
    /// Also red on <c>ProviderContextMiddleware</c>'s <c>deadline.BindTo</c>, same
    /// sentence with 20.1s — without it the deadline falls back to its construction
    /// backstop, which is a bound on the wrong clock.
    /// </summary>
    [Fact]
    public async Task The_ceiling_is_the_request_bound_plus_the_uncancellable_tail()
    {
        var providerPublicId = await SeedProviderAsync();

        var requestBound = TimeSpan.FromSeconds(2);
        var grace = TimeSpan.FromSeconds(2);
        var wedged = TimeSpan.FromSeconds(20);

        using var stalled = new PracticeApiFactory(sql.ConnectionString, services =>
        {
            FailureHarness.With(
                sql.ConnectionString,
                new StallsEveryStatementAgainst("AuditEvents", wedged))(services);

            FailureHarness.BoundedBy(services, backstop: TimeSpan.FromSeconds(60), grace);

            services.Configure<RequestTimeoutOptions>(options => options.DefaultPolicy =
                new RequestTimeoutPolicy { Timeout = requestBound });
        });

        using var client = ClientFor(stalled, providerPublicId);

        // Warm the host, the pool and the query plans, so the measurement below is of the
        // request and not of everything a first request drags in with it.
        (await client.GetAsync("/health/live")).Dispose();

        /*
         * DELETE of an id that exists nowhere — F2's own example.
         *
         * It is the shortest path in the API to an uncancellable write: two reads that
         * answer at once, then a refusal audit row that is the ONLY thing the attempt
         * leaves behind (D052, docs/SECURITY.md §Audit). Nothing else is in the way, so
         * what the clock measures is the tail.
         */
        var started = Stopwatch.GetTimestamp();
        using var response = await client.DeleteAsync($"/notes/{Guid.NewGuid()}");
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.True(
            elapsed > requestBound,
            $"The response arrived in {elapsed.TotalSeconds:0.0}s, within the "
            + $"{requestBound.TotalSeconds:0}s request bound. This test has to reach an "
            + "uncancellable write that is still running when that bound fires, or it "
            + "measures nothing — check that the audit table is the one being stalled.");

        // The grace, plus room for the host: the assertion is about a bound holding, not
        // about the exact moment it fires, and a machine under load must not turn a
        // 4-second answer into a failure. The alternative it is separating itself from is
        // 20 seconds.
        var ceiling = requestBound + grace + TimeSpan.FromSeconds(3);

        Assert.True(
            elapsed < ceiling,
            $"The audit write ran for {elapsed.TotalSeconds:0.0}s past a "
            + $"{requestBound.TotalSeconds:0}s request bound. RequestTimeoutsMiddleware "
            + "cancels RequestAborted and then waits, so an uncancellable write ADDS to "
            + "the request bound rather than nesting inside it. DatabaseTimeouts.Ceiling "
            + "is only true while something bounds that tail.");
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
    /// COMPARED AGAINST THE CEILING, NOT THE REQUEST BOUND, and that is this round's
    /// correction. The previous version asserted <c>bff &gt; DatabaseTimeouts.Request</c>
    /// and was green while the real ceiling — the request bound PLUS the uncancellable
    /// audit tail — sat above the BFF's timeout. It compared the BFF with the wrong number
    /// and therefore could not see the inversion it existed to catch.
    /// <see cref="The_ceiling_is_the_request_bound_plus_the_uncancellable_tail"/> is what
    /// establishes that the number on the right is the real one.
    ///
    /// Control: <c>API_TIMEOUT_MS</c> in web/lib/api/timeouts.ts.
    /// Lowered to the 300_000 it held before this round — which is above
    /// <c>DatabaseTimeouts.Request</c> and below <c>DatabaseTimeouts.Ceiling</c>, so it is
    /// also the deletion that shows the comparison changed — → red, "The BFF gives up
    /// after 00:05:00 while this API's ceiling on a request is 00:11:50. The tier that
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
            bff > DatabaseTimeouts.Ceiling,
            $"The BFF gives up after {bff} while this API's ceiling on a request is "
            + $"{DatabaseTimeouts.Ceiling}. The tier that gives up first decides the "
            + "bound, so a shorter BFF timeout silently replaces every number on "
            + "DatabaseTimeouts — including the retry budget it is sized around.");
    }

    // ------------------- the document a compliance reviewer reads (1.17 F3)

    /// <summary>
    /// docs/SECURITY.md names the token an audit write ACTUALLY runs on.
    ///
    /// THIS IS THE ONE CLASS OF DEFECT THIS REPOSITORY KEEPS SHIPPING, and it has now been
    /// found five times (D072): a control described in prose, in a file nothing compiles,
    /// with nothing able to notice it going stale. §Audit asserted "No audit write is
    /// cancellable. IAuditWriter.WriteAsync takes no CancellationToken, and AuditWriter
    /// saves on CancellationToken.None" for two commits after D090 replaced that token with
    /// a bounded per-request deadline — so the document a compliance reviewer reads DENIED a
    /// durability gap the codebase had knowingly accepted, and D012's append-only framing
    /// rested on the denial.
    ///
    /// A DOCUMENT THAT OVERSTATES A GUARANTEE IS WORSE THAN ONE THAT ADMITS A LIMIT. The
    /// second gets the limit reviewed; the first gets the question closed.
    ///
    /// Read out of both files rather than described in either, the same way
    /// <see cref="The_bff_waits_longer_than_this_api_is_prepared_to_spend"/> reads
    /// API_TIMEOUT_MS out of the web tree: two trees, one claim, and a test that fails when
    /// either side moves. Reverting the code to <c>CancellationToken.None</c> fails this
    /// just as surely as leaving the sentence behind does, which is what makes it a guard
    /// rather than a spell-check.
    ///
    /// Control: the sentence "<c>AuditWriter</c> saves on <c>deadline.Token</c>" in
    /// docs/SECURITY.md §Audit.
    /// Reverted to the <c>CancellationToken.None</c> this round replaced — the falsification
    /// rather than a deletion, because the defect is a WRONG claim and not a missing one —
    /// → red, "Assert.Equal() Failure: Strings differ, Expected: "deadline.Token", Actual:
    /// "CancellationToken.None"".
    /// </summary>
    [Fact]
    public void The_security_document_names_the_token_audit_writes_run_on()
    {
        var writer = File.ReadAllText(
            RepoFile("api/src/Practice.Infrastructure/Identity/ProviderAuthenticator.cs"));

        var save = Regex.Match(
            writer,
            @"await db\.SaveChangesAsync\(([^)]+)\);",
            RegexOptions.None,
            TimeSpan.FromSeconds(1));

        Assert.True(
            save.Success,
            "AuditWriter.WriteAsync no longer saves in a shape this test can read. The "
            + "token an audit write runs on is a compliance claim in docs/SECURITY.md, and "
            + "nothing else relates the two.");

        var security = File.ReadAllText(RepoFile("docs/SECURITY.md"));

        var claimed = Regex.Match(
            security,
            @"`AuditWriter` saves on `([^`]+)`",
            RegexOptions.None,
            TimeSpan.FromSeconds(1));

        Assert.True(
            claimed.Success,
            "docs/SECURITY.md §Audit no longer states which token AuditWriter saves on. It "
            + "is the sentence a compliance reviewer reads to decide whether the audit "
            + "trail can lose a row, so it has to be there and it has to be right.");

        Assert.Equal(save.Groups[1].Value, claimed.Groups[1].Value);
    }

    /// <summary>
    /// EVERY call of the notification seam is bounded — the tree is walked, not listed.
    ///
    /// There is one call site today and the measured test in ConsultationIntakeTests
    /// exercises it. This is the other half: a guard that means "all of them" and holds a
    /// hard-coded list is a test about the day it was written, and stays green exactly when
    /// the set grows (docs/TEST_STRATEGY.md; five of these shipped here before D090 swept
    /// for them). A second caller — a resend from the inbox, a retry job — arrives bounded
    /// or arrives red.
    ///
    /// It matches INVOCATIONS, not declarations: the leading dot is what separates
    /// <c>notifier.NotifyAsync(id)</c> from <c>Task NotifyAsync(Guid id)</c> on the
    /// interface and its implementations.
    ///
    /// Control: the <c>.WaitAsync(deadline.Token)</c> on the notifier call in
    /// ConsultationEndpoints.SubmitConsultationRequest.
    /// Deleted → red, "IConsultationNotifier holds no CancellationToken by design (D079),
    /// so the call site is the only place its work can be bounded. Unbounded call(s):
    /// ConsultationEndpoints.cs:305. …"
    /// </summary>
    [Fact]
    public void Every_call_of_the_notification_seam_is_bounded()
    {
        var unbounded = Directory
            .EnumerateFiles(RepoFile("api/src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => File.ReadAllLines(path)
                .Select((text, index) => (Path: path, Line: index + 1, Text: text))
                .Where(line => line.Text.Contains(".NotifyAsync(", StringComparison.Ordinal))
                .Where(line => !line.Text.Contains(
                    ".WaitAsync(deadline.Token)", StringComparison.Ordinal)))
            .Select(line => $"{Path.GetFileName(line.Path)}:{line.Line}")
            .ToArray();

        Assert.True(
            unbounded.Length == 0,
            "IConsultationNotifier holds no CancellationToken by design (D079), so the call "
            + "site is the only place its work can be bounded. Unbounded call(s): "
            + string.Join(", ", unbounded)
            + ". A notifier that outlives DatabaseTimeouts.Ceiling moves the number the "
            + "BFF's API_TIMEOUT_MS is sized against, and the real mail transport is a "
            + "network call to somebody else's infrastructure.");
    }

    /// <summary>
    /// A path inside the repository, found by walking up from the test assembly.
    ///
    /// The build output sits several directories below the tree, and the depth differs
    /// between a local run and CI. Walking up to the thing being asserted on is the version
    /// that does not encode either.
    ///
    /// Files AND directories, because one of the guards above walks a whole source tree
    /// rather than naming the files in it — which is the point of that guard.
    /// </summary>
    private static string RepoFile(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
             dir is not null;
             dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
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
    ///
    /// <see cref="ProtectedMember"/> does the lookup, shared with the Identity manager's
    /// <c>CancellationToken</c> — the same problem, one framework type over.
    /// </summary>
    private static (int Retries, TimeSpan Backoff) RetryPolicyOf(IExecutionStrategy strategy)
    {
        var retries = ProtectedMember.Read(strategy, "MaxRetryCount");
        var backoff = ProtectedMember.Read(strategy, "MaxRetryDelay");

        return ((int)retries, (TimeSpan)backoff);
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
