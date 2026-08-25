using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Practice.Api.Auth;
using Practice.Api.RateLimiting;
using Practice.Domain.Auditing;
using Practice.Domain.Providers;
using Practice.Infrastructure.Identity;
using Practice.Infrastructure.Persistence;
using Practice.Infrastructure.RateLimiting;

namespace Practice.Api.Tests;

/// <summary>
/// The limiter in front of the credential path, against real SQL Server.
///
/// WHAT THIS EXISTS TO CLOSE, in one sentence: the five-failure lockout can only count
/// attempts against an account that EXISTS, so a stream of guesses at addresses nobody has
/// registered was bounded by nothing in this tier at all — and every one of them woke a
/// container that scales from zero, ran a PBKDF2 hash and inserted an audit row. That is
/// D097's own closing note, and WORK_QUEUE 1.19 is it.
///
/// EVERY TEST HERE GIVES ITSELF A SOURCE, and that is not incidental. The suite shares one
/// database, so a class whose tests all fell into the same partition would be measuring each
/// other's leftovers — the same shared-bucket defect D068 found in the E2E suite, one tier
/// along. A random 64-hex key per test is what the deployed topology looks like anyway:
/// behind ingress, different callers arrive with different forwarded keys.
///
/// SYNTHETIC DATA ONLY. No address, real or otherwise, reaches a counter row — the store
/// holds hashes, which is the whole design (see RateLimitPartition).
/// </summary>
[Collection(UsesSqlServer.Name)]
public sealed class RateLimitTests(SqlServerFixture sql) : IAsyncLifetime, IDisposable
{
    private PracticeApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new PracticeApiFactory(sql.ConnectionString);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _factory?.Dispose();

    private const string Password = "correct-horse-battery-staple";

    // ------------------------------------------------------- the hole 1.19 exists to close

    /// <summary>
    /// A stream of guesses at addresses that have no account is refused.
    ///
    /// THIS IS THE MEASUREMENT THE TASK IS ABOUT. Every request here names an address that
    /// has never existed, so <c>AccessFailedCount</c> has no row to move and the lockout
    /// counts precisely zero of them however many arrive. Before the limiter, all
    /// twenty-one were answered 200 <c>{"status":"invalid"}</c> — and each one had already
    /// cost a container wake-up, a PBKDF2 hash and an audit row by the time it said so.
    ///
    /// The bound is the SOURCE partition rather than the account one: twenty-one different
    /// addresses is twenty-one different account buckets, which is exactly why an
    /// account-only limiter counts to one forever against an address list.
    ///
    /// Control: the <c>.RateLimitBySource(policies => policies.LoginPerSource)</c> on the
    /// <c>/auth</c> group in AuthEndpoints.
    /// Deleted → red, "21 guesses at 21 different addresses that have no account were all
    /// answered 200. The five-failure lockout counts nothing here — there is no row to
    /// increment — so nothing in this tier bounds the stream at all." Which is the state of
    /// the code the task was written against, reproduced.
    /// </summary>
    [Fact]
    public async Task A_flood_of_guesses_at_addresses_with_no_account_is_refused()
    {
        var limit = new RateLimitPolicies().LoginPerSource.Limit;

        using var client = ClientWith(_factory, SyntheticSourceKey());

        var answers = new List<HttpStatusCode>();

        for (var i = 0; i <= limit; i++)
        {
            answers.Add((await AttemptAsync(
                client, $"nobody-{Guid.NewGuid():N}@example.com", "guess")).Status);
        }

        Assert.True(
            answers[^1] == HttpStatusCode.TooManyRequests,
            $"{limit + 1} guesses at {limit + 1} different addresses that have no account "
            + $"were all answered {(int)answers[^1]}. The five-failure lockout counts "
            + "nothing here — there is no row to increment — so nothing in this tier bounds "
            + "the stream at all, and every one of them wakes a container that scales from "
            + "zero, runs a PBKDF2 hash and inserts an audit row.");

        Assert.All(
            answers[..limit],
            status => Assert.Equal(HttpStatusCode.OK, status));
    }

    /// <summary>
    /// A caller who rotates the forwarded key cannot mint a fresh bucket per request.
    ///
    /// D080's FINDING, ONE TIER ALONG. The consultation limiter used to read the LEFT-hand
    /// entry of <c>x-forwarded-for</c> — the half the caller writes — so rotating it put
    /// every submission in its own bucket and the control was worth nothing against the
    /// traffic it existed to stop. The same hole is available here in a different shape: the
    /// source key arrives on a header, so anything that can reach this tier can send
    /// whatever it likes on it, and a limiter that keys on arbitrary text counts to one
    /// forever.
    ///
    /// The answer is not to trust the header more — 4.4 is what would do that — it is to
    /// accept it only in the ONE shape this application produces. Twenty-one requests, each
    /// with a different key that is not a SHA-256 digest, all land in the shared
    /// unattributed bucket and the twenty-first is refused.
    ///
    /// Control: the length and hex validation in <c>RateLimitPartition.SourceKey</c> —
    /// the <c>if (…) return Unattributed;</c> and the loop after it.
    /// Both deleted, so the header is returned as sent → red, "21 requests carrying 21
    /// different made-up client keys were all answered 200. A caller that can choose its own
    /// partition is not partitioned."
    /// </summary>
    [Fact]
    public async Task A_caller_cannot_mint_a_fresh_bucket_by_making_up_a_key()
    {
        var limit = new RateLimitPolicies().LoginPerSource.Limit;

        var answers = new List<HttpStatusCode>();

        for (var i = 0; i <= limit; i++)
        {
            // Not a digest: the wrong length, and characters no hex string contains.
            using var client = ClientWith(_factory, $"rotating-key-{Guid.NewGuid():N}");

            answers.Add((await AttemptAsync(
                client, $"nobody-{Guid.NewGuid():N}@example.com", "guess")).Status);
        }

        Assert.True(
            answers[^1] == HttpStatusCode.TooManyRequests,
            $"{limit + 1} requests carrying {limit + 1} different made-up client keys were "
            + $"all answered {(int)answers[^1]}. A caller that can choose its own partition "
            + "is not partitioned — which is the defect D080 found on the leading entry of "
            + "x-forwarded-for, reachable here through a header instead.");
    }

    // -------------------------------------------------- the 429 is not a fresh oracle

    /// <summary>
    /// The refusal is byte-for-byte the same for an address that has an account and one that
    /// does not — and neither of them reaches the users table.
    ///
    /// MEASURED, NOT ARGUED, and the instrument is the same one 1.18 F1 used: every
    /// statement against <c>AspNetUsers</c> is stalled 1.5 seconds. A refused request that
    /// answers in well under that has not looked an account up, which is both halves of the
    /// claim at once —
    ///
    ///   * it cannot be an enumeration oracle in the CLOCK, because the one operation whose
    ///     cost depends on whether the account exists never runs;
    ///   * and it cannot be one in COST either, which is the point of the limiter: a refused
    ///     guess must not buy a PBKDF2 hash and an audit row on a container that scales from
    ///     zero.
    ///
    /// The two are driven to the limit on SEPARATE source keys, so the dimension under test
    /// is the account one — the partition derived from what was TYPED rather than from a row
    /// that was found. Both are compared one request PAST the crossing, so neither is the
    /// request that writes the audit row and the comparison is of steady-state refusals.
    ///
    /// Status, body bytes, and the full header set are compared, because 1.18 F1 was an
    /// oracle in status AND body AND time simultaneously and a test that read one field
    /// stayed green through all three.
    ///
    /// Control: the <c>.RateLimitByAccount&lt;PasswordRequest&gt;(…, request =&gt;
    /// request.Email)</c> on <c>/auth/password</c>.
    /// Deleted → red on the premise, "The unknown address was answered 200 and the known one
    /// 200. Neither was refused, so this test is not comparing two refusals." Nothing is
    /// account-limited without it, and the hole the whole task is about — an address with no
    /// row being counted by nothing — is back.
    ///
    /// Control: the <c>if (decision.Allowed) return await next(context);</c> early return in
    /// RateLimitFilter, which is what stops a refused request reaching the endpoint.
    /// Changed to <c>var passed = await next(context); if (decision.Allowed) return passed;</c>
    /// → red, "Assert.All() Failure: 2 out of 2 items in the collection did not pass. … A
    /// refusal took 3070 ms against a users table stalled 1500 ms per statement, so the
    /// refused request reached AspNetUsers." Both of them, and two stalls' worth each. The
    /// refusal still answers 429 with the same bytes; it just pays for the credential path
    /// first, which is a limiter that limits nothing expensive.
    /// </summary>
    [Fact]
    public async Task The_refusal_is_the_same_for_an_address_with_an_account_and_one_without()
    {
        var (_, known) = await SeedProviderAsync();
        var unknown = $"nobody-{Guid.NewGuid():N}@example.com";

        var stall = TimeSpan.FromMilliseconds(1500);

        // Two attempts each is enough to cross a limit of two, and each one pays the stall —
        // so the limit is deliberately small rather than production's ten.
        using var stalled = FactoryWith(
            new RateLimitPolicies
            {
                LoginPerAccount = new("login-account", 2, TimeSpan.FromMinutes(15), false),
            },
            new StallsEveryStatementAgainst("AspNetUsers", stall));

        using var knownClient = ClientWith(stalled, SyntheticSourceKey());
        using var unknownClient = ClientWith(stalled, SyntheticSourceKey());

        // The host, the pool and the password hasher's first PBKDF2, paid once and by
        // neither of the measurements below.
        (await stalled.CreateClient().GetAsync("/health/live")).Dispose();

        // Two allowed, one crossing (which writes the audit row), then the one measured.
        for (var i = 0; i < 3; i++)
        {
            await AttemptAsync(knownClient, known, "wrong-password-here");
            await AttemptAsync(unknownClient, unknown, "wrong-password-here");
        }

        var knownRefusal = await AttemptAsync(knownClient, known, "wrong-password-here");
        var unknownRefusal = await AttemptAsync(unknownClient, unknown, "wrong-password-here");

        Assert.True(
            knownRefusal.Status == HttpStatusCode.TooManyRequests
            && unknownRefusal.Status == HttpStatusCode.TooManyRequests,
            $"The unknown address was answered {(int)unknownRefusal.Status} and the known "
            + $"one {(int)knownRefusal.Status}. Neither was refused, so this test is not "
            + "comparing two refusals — the account partition is what makes an address with "
            + "no row countable at all.");

        Assert.Equal(knownRefusal.Body, unknownRefusal.Body);
        Assert.Equal(string.Empty, knownRefusal.Body);
        Assert.Equal(knownRefusal.Headers, unknownRefusal.Headers);

        Assert.All(
            new[] { knownRefusal, unknownRefusal },
            answer => Assert.True(
                answer.Elapsed < stall,
                $"A refusal took {answer.Elapsed.TotalMilliseconds:0} ms against a users "
                + $"table stalled {stall.TotalMilliseconds:0} ms per statement, so the "
                + "refused request reached AspNetUsers. A limiter that runs after the "
                + "credential path limits nothing that costs anything, and the time it "
                + "spends there is the difference between an address that exists and one "
                + "that does not."));
    }

    /// <summary>
    /// Neither login policy announces when the window ends.
    ///
    /// A REAL DECISION AND NOT AN OVERSIGHT (D098). <c>Retry-After</c> helps a legitimate
    /// client and helps an attacker pace exactly at the limit with no wasted requests, and
    /// on THIS path there is no legitimate client that needs it: <c>web</c> renders one
    /// fixed sentence with no number in it. What the header would add is one more field that
    /// has to be proved identical for an address with an account and one without, on the
    /// path whose whole difficulty is that it must not become an enumeration oracle. The
    /// dimension cannot leak because it does not exist.
    ///
    /// The dictation-upload policy answers the opposite way, for a caller that is already
    /// authenticated and has a take it must eventually land — asserted here as the pair,
    /// because "we thought about it" is only true if the two differ.
    ///
    /// Control: <c>AnnouncesRetryAfter: false</c> on <c>RateLimitPolicies.LoginPerSource</c>.
    /// Flipped to true → red, "The login refusal carried Retry-After: 300. That number tells
    /// an attacker exactly when to resume".
    /// </summary>
    [Fact]
    public async Task The_login_refusal_says_nothing_about_when_to_come_back()
    {
        var limit = new RateLimitPolicies().LoginPerSource.Limit;

        using var client = ClientWith(_factory, SyntheticSourceKey());

        Answer refusal = default!;

        for (var i = 0; i <= limit; i++)
        {
            refusal = await AttemptAsync(
                client, $"nobody-{Guid.NewGuid():N}@example.com", "guess");
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, refusal.Status);

        Assert.False(
            refusal.Headers.ContainsKey("Retry-After"),
            "The login refusal carried Retry-After: "
            + (refusal.Headers.GetValueOrDefault("Retry-After") ?? "?")
            + ". That number tells an attacker exactly when to resume, so a caller who "
            + "honours it spends no request finding out — and it is one more field that "
            + "would have to be identical for an address that has an account and one that "
            + "does not (D098).");

        var policies = new RateLimitPolicies();

        Assert.False(policies.LoginPerAccount.AnnouncesRetryAfter);
        Assert.True(
            policies.DictationUpload.AnnouncesRetryAfter,
            "The dictation upload policy withholds Retry-After too, so this is a habit "
            + "rather than a decision. That caller is authenticated and is a resumable "
            + "uploader with a take it has to land: withholding the number turns a client "
            + "that would have waited into one that retries blind.");
    }

    // ------------------------------------------------------------- the store is shared

    /// <summary>
    /// The count holds across hosts, because it is not in any host's memory.
    ///
    /// THE REASON 1.18 REFUSED TO SHIP A LIMITER AT ALL. <c>api</c> is a Container App that
    /// scales horizontally and to zero, so an in-process counter limits one replica — an
    /// attacker round-robins the rest and multiplies the limit by the replica count — and it
    /// forgets everything the moment the last replica is scaled away. Shipping that inside a
    /// commit whose whole subject was a control that looked present and was not would have
    /// been the same defect again, which is why the store is a table.
    ///
    /// TWO HOSTS, ONE DATABASE. Each <c>PracticeApiFactory</c> builds its own service
    /// provider, its own singletons and its own DbContext pool, so anything held in host
    /// state is not shared between them. WHAT THIS CANNOT PROVE, said plainly rather than
    /// implied: they run in one process, so this is not evidence about a second machine —
    /// nothing in this repository can produce that. It is evidence that the counter is not
    /// in per-host state, which is the property that was actually at risk.
    ///
    /// Control: the partition key — that it is derived from the policy and the caller and
    /// NOTHING about the host.
    /// Falsified rather than deleted, because there is no line to delete: a
    /// <c>Guid Instance { get; } = Guid.NewGuid()</c> added to <c>RateLimitPolicies</c> (a
    /// per-host singleton) and appended to the key in RateLimitFilter, which is exactly what
    /// "the counter is per replica" looks like → red, "The second host answered 200 for a
    /// request the first host had already spent the whole allowance of 20 on. An attacker
    /// with two replicas to choose from gets two allowances, and a container app that scales
    /// to zero forgets the count entirely between them."
    ///
    /// TEN OF THE TWELVE TESTS IN THIS CLASS STAY GREEN UNDER THAT EDIT, which is the finding
    /// rather than a footnote: a per-replica limiter is invisible to every test that talks to
    /// one replica, and that is the shape of control WORK_QUEUE 1.18 refused to ship. The
    /// eleventh, <see cref="A_request_the_caller_abandons_is_still_counted"/>, goes red for an
    /// unrelated reason — it reads the counter row back by key, so a key with a host in it is
    /// a key it cannot find.
    /// </summary>
    [Fact]
    public async Task The_limit_holds_across_hosts_that_share_a_database()
    {
        var limit = new RateLimitPolicies().LoginPerSource.Limit;
        var source = SyntheticSourceKey();

        using var second = new PracticeApiFactory(sql.ConnectionString);

        using var onFirst = ClientWith(_factory, source);
        using var onSecond = ClientWith(second, source);

        for (var i = 0; i < limit; i++)
        {
            await AttemptAsync(onFirst, $"nobody-{Guid.NewGuid():N}@example.com", "guess");
        }

        var answer = await AttemptAsync(
            onSecond, $"nobody-{Guid.NewGuid():N}@example.com", "guess");

        Assert.True(
            answer.Status == HttpStatusCode.TooManyRequests,
            $"The second host answered {(int)answer.Status} for a request the first host had "
            + $"already spent the whole allowance of {limit} on. An attacker with two "
            + "replicas to choose from gets two allowances, and a container app that scales "
            + "to zero forgets the count entirely between them.");
    }

    /// <summary>
    /// Simultaneous requests are all counted.
    ///
    /// THE DEFECT THIS SHAPE HAS ALREADY PRODUCED ONCE HERE, measured, in the commit before
    /// this one: four waves of twenty concurrent wrong passwords left
    /// <c>AccessFailedCount = 4</c>, because <c>UserManager.AccessFailedAsync</c> is a
    /// read-modify-write behind an optimistic stamp. A limiter with the same shape raises its
    /// own ceiling by however wide the caller can be — which is worse than the lockout
    /// version, because a limiter is the control that is SUPPOSED to be looking at exactly
    /// this traffic.
    ///
    /// So the count is one UPDATE the engine serialises, and this is what says so: forty
    /// requests at once against a limit of five must produce thirty-five refusals. A
    /// read-modify-write would produce a handful.
    ///
    /// MEASURED UNDER CONTENTION rather than reasoned about. The forty are issued together
    /// and the assertion is on the tally, not on a sequence.
    ///
    /// Control: the single <c>UPDATE … SET [Count] = CASE … ELSE [Count] + 1 END</c> in
    /// SqlRateLimitStore — that the count on the right-hand side is the ROW's, read by the
    /// engine under the lock it is already holding.
    /// Falsified to the read-modify-write it replaces: the count fetched by a SEPARATE
    /// <c>SqlQuery&lt;int&gt;</c> round trip and interpolated into the UPDATE as a constant,
    /// which is the shape <c>UserManager.AccessFailedAsync</c> has → red, "40 simultaneous
    /// requests against a limit of 5 produced 32 refusals; 35 were expected." Three
    /// increments lost out of forty.
    ///
    /// A FIRST ATTEMPT AT THAT FALSIFICATION STAYED GREEN, and the reason is worth keeping
    /// because it is a second control covering for the first (docs/TEST_STRATEGY.md). Moving
    /// the read into a <c>DECLARE @current</c> at the top of the SAME batch, transaction and
    /// hints removed, changed nothing: under READ COMMITTED a reader BLOCKS on the exclusive
    /// lock the previous request's UPDATE is holding, so the engine serialised the
    /// read-modify-write anyway. The race only opens when the read is a round trip of its
    /// own, which is exactly what a read-modify-write through an ORM is.
    /// </summary>
    [Fact]
    public async Task Simultaneous_requests_are_all_counted()
    {
        const int limit = 5;
        const int callers = 40;

        using var tight = FactoryWith(new RateLimitPolicies
        {
            LoginPerSource = new("login-source", limit, TimeSpan.FromMinutes(5), false),
        });

        using var client = ClientWith(tight, SyntheticSourceKey());
        (await client.GetAsync("/health/live")).Dispose();

        var answers = await Task.WhenAll(Enumerable.Range(0, callers).Select(_ =>
            AttemptAsync(client, $"nobody-{Guid.NewGuid():N}@example.com", "guess")));

        var refused = answers.Count(a => a.Status == HttpStatusCode.TooManyRequests);

        Assert.True(
            refused == callers - limit,
            $"{callers} simultaneous requests against a limit of {limit} produced {refused} "
            + $"refusals; {callers - limit} were expected. A counter that loses increments "
            + "under contention raises its own ceiling by the width of the attack — which is "
            + "the traffic a rate limiter exists to see, so losing it there is worse than "
            + "losing it on the lockout (D097).");
    }

    /// <summary>
    /// A new window restores the allowance.
    ///
    /// The dull half of a fixed window, and it is worth pinning because the failure is
    /// silent in the direction that hurts the only legitimate user: a counter that never
    /// rolls locks Michelle out of her own practice permanently, and every test above would
    /// stay green.
    ///
    /// MEASURED WITH A REAL WAIT against a two-second window, rather than faked with a clock
    /// the store does not use in production. A test that moves a fake clock proves the
    /// arithmetic; this proves the statement.
    ///
    /// Control: the <c>WHEN [WindowEndsAtUtc] &lt;= {now} THEN 1</c> arm of the count CASE in
    /// SqlRateLimitStore — the one that starts a new window.
    /// Removed, leaving the count to carry on climbing → red, "The allowance had not come
    /// back 3.0s after a 2s window: the request was answered 429."
    /// </summary>
    [Fact]
    public async Task A_new_window_restores_the_allowance()
    {
        var window = TimeSpan.FromSeconds(2);

        using var brief = FactoryWith(new RateLimitPolicies
        {
            LoginPerSource = new("login-source", 2, window, false),
        });

        using var client = ClientWith(brief, SyntheticSourceKey());
        (await client.GetAsync("/health/live")).Dispose();

        for (var i = 0; i < 3; i++)
        {
            await AttemptAsync(client, $"nobody-{Guid.NewGuid():N}@example.com", "guess");
        }

        var waited = window + TimeSpan.FromSeconds(1);
        await Task.Delay(waited);

        var answer = await AttemptAsync(
            client, $"nobody-{Guid.NewGuid():N}@example.com", "guess");

        Assert.True(
            answer.Status == HttpStatusCode.OK,
            $"The allowance had not come back {waited.TotalSeconds:0.0}s after a "
            + $"{window.TotalSeconds:0}s window: the request was answered "
            + $"{(int)answer.Status}. A counter that does not roll is a permanent lockout "
            + "for the one person who has a legitimate reason to be here.");
    }

    // ------------------------------------------------------------------ the audit trail

    /// <summary>
    /// A limiter nobody can see firing is a control that looks present.
    ///
    /// The refusal is deliberately contentless and the response is identical to every other
    /// refusal, so — exactly as with the unknown-email row — nothing else in this system
    /// records that it happened. The audit row is the only place a burst leaves a mark.
    ///
    /// ONE ROW PER PARTITION PER WINDOW, NOT ONE PER REFUSED REQUEST, and that is a decision
    /// with a cost rather than an optimisation. A row per request would make AuditEvents the
    /// amplification target the limiter exists to close: a caller who is being refused would
    /// still be inserting rows at whatever rate they can dial, into the one table this
    /// application never deletes from. What it costs is that a burst which fits inside a
    /// single window and loses its one row leaves nothing; a sustained one writes another row
    /// next window.
    ///
    /// AND THE ROW CARRIES NO ADDRESS. Not the one that was typed — a table of the addresses
    /// somebody guessed is the enumeration list this whole control exists to deny, in the
    /// table most likely to be exported to a SIEM (docs/SECURITY.md §Audit). The source hash
    /// it does carry is the same value <c>ConsultationRequest.SourceIpHash</c> holds, so "did
    /// these attempts come from the same place as that enquiry" stays answerable.
    ///
    /// Control: the <c>if (decision.CrossedTheLimit)</c> guard in RateLimitFilter.
    /// Deleted, so every refusal writes → red, "8 RateLimited rows were written for one burst
    /// of 11 requests against a limit of 3 on the source partition. A row per refused request
    /// makes AuditEvents the amplification target the limiter exists to close."
    ///
    /// Control: the metadata string in that same block — that it carries the policy and the
    /// dimension and NOT the submitted address.
    /// Falsified by appending <c>;account={partitionValue(context)}</c> → red,
    /// "Assert.All() Failure: 11 out of 11 items in the collection did not pass. [0]: Item:
    /// "nobody-df200fd5…@example.co"··· Error: Assert.DoesNotContain() Failure: Sub-string
    /// found ↓ (pos 55) String: ···"unt;limit=3;account=f21511c78c7c08e75f822"···".
    ///
    /// THAT FALSIFICATION STAYED GREEN UNTIL THIS TEST GREW ITS SECOND BURST, and that is the
    /// reason the second burst exists. With only the source flood, the value the row would
    /// have leaked is a hashed SOURCE — the leak this test names is only in scope on the
    /// partition whose key IS a submitted address, and nothing here was tripping it. A test
    /// that cannot reach the case it describes is the D066 defect, found by running the
    /// deletion rather than by reading the assertion.
    /// </summary>
    [Fact]
    public async Task A_burst_leaves_one_audit_row_per_window_and_no_address_in_it()
    {
        const int limit = 3;

        /*
         * BOTH DIMENSIONS, and the second one is not decoration.
         *
         * The first burst walks an address list from one source, so it trips the SOURCE
         * partition — and the value that partition is keyed on is a hashed address that has
         * nothing to do with any account. Written on its own, this test cannot see an address
         * reaching a row AT ALL: the leak it exists to prevent is only in scope on the
         * partition whose key IS a submitted address. Found by deleting the control and
         * watching it stay green, which is the whole reason the protocol runs.
         */
        var sourceBurst = await BurstAsync(
            new RateLimitPolicies
            {
                LoginPerSource = new("login-source", limit, TimeSpan.FromMinutes(5), false),
            },
            attempts: limit + 8,
            oneAddress: false);

        var accountBurst = await BurstAsync(
            new RateLimitPolicies
            {
                // Wide enough that the SOURCE dimension cannot be what trips.
                LoginPerSource = new("login-source", 500, TimeSpan.FromMinutes(5), false),
                LoginPerAccount = new("login-account", limit, TimeSpan.FromMinutes(15), false),
            },
            attempts: limit + 8,
            oneAddress: true);

        foreach (var (burst, dimension) in new[]
        {
            (sourceBurst, "source"),
            (accountBurst, "account"),
        })
        {
            Assert.True(
                burst.Rows.Count == 1,
                $"{burst.Rows.Count} RateLimited rows were written for one burst of "
                + $"{limit + 8} requests against a limit of {limit} on the {dimension} "
                + "partition. A row per refused request makes AuditEvents the amplification "
                + "target the limiter exists to close, in the one table this application "
                + "never deletes from; none at all makes the limiter invisible, which is the "
                + "control-that-looks-present defect.");

            var row = burst.Rows[0];

            Assert.Equal(AuditOutcome.Denied, row.Outcome);
            Assert.Equal(burst.Source, row.IpAddress);
            Assert.Contains($"partition={dimension}", row.Metadata!, StringComparison.Ordinal);
            Assert.Contains($"policy=login-{dimension}", row.Metadata!, StringComparison.Ordinal);

            var written = $"{row.Metadata}|{row.IpAddress}|{row.EntityType}";

            Assert.All(burst.Guessed, address =>
            {
                Assert.DoesNotContain(address, written, StringComparison.OrdinalIgnoreCase);

                Assert.DoesNotContain(
                    RateLimitPartition.AccountKey(address), written, StringComparison.Ordinal);
            });
        }
    }

    private sealed record Burst(
        string Source, IReadOnlyList<string> Guessed, IReadOnlyList<AuditEvent> Rows);

    /// <summary>
    /// One flood, and the rows it left. <paramref name="oneAddress"/> chooses which dimension
    /// it trips: a list of addresses exhausts the source bucket, one address repeated
    /// exhausts that address's own.
    /// </summary>
    private async Task<Burst> BurstAsync(
        RateLimitPolicies policies, int attempts, bool oneAddress)
    {
        var source = SyntheticSourceKey();
        var watermark = await AuditWatermarkAsync();
        var only = $"nobody-{Guid.NewGuid():N}@example.com";

        using var tight = FactoryWith(policies);
        using var client = ClientWith(tight, source);

        var guessed = new List<string>();

        for (var i = 0; i < attempts; i++)
        {
            var address = oneAddress ? only : $"nobody-{Guid.NewGuid():N}@example.com";
            guessed.Add(address);
            await AttemptAsync(client, address, "guess");
        }

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var rows = await db.AuditEvents.AsNoTracking()
            .Where(e => e.Id > watermark && e.EventType == AuditEventType.RateLimited)
            .ToListAsync();

        return new Burst(source, guessed, rows);
    }

    /// <summary>
    /// A refused request never reaches the credential path.
    ///
    /// THE COST IS THE THREAT, not the guess. Every unbounded attempt woke a container that
    /// scales from zero, ran a PBKDF2 hash deliberately tuned to be slow, and inserted an
    /// audit row — so an attacker who never guesses right still spends this practice's money
    /// at whatever rate they choose. A limiter that runs after all of that is a limiter that
    /// changes nothing.
    ///
    /// Asserted from the other side rather than by timing: a refused attempt against an
    /// address that DOES have an account leaves no <c>LoginFailed</c> row and does not move
    /// <c>AccessFailedCount</c>. Both are things the credential path does and nothing else
    /// does, so their absence is proof the path did not run.
    ///
    /// IT ALSO MEANS A REFUSED ATTEMPT IS NOT A COUNTED FAILURE, which is the right way
    /// round and worth stating: the lockout counts credentials that were CHECKED and refused.
    /// Counting requests that were never evaluated would let an attacker who cannot reach the
    /// password check at all still lock Michelle out of her own account.
    ///
    /// Control: the <c>if (decision.Allowed) return await next(context);</c> early return in
    /// RateLimitFilter.
    /// Changed to always call <c>next(context)</c> and then answer 429 → red, "A refused
    /// attempt left 1 LoginFailed row(s) and moved AccessFailedCount from 2 to 3. The refusal
    /// is supposed to happen before the PBKDF2 hash, the account lookup and the audit write."
    /// The response is still a 429; everything the limiter exists to save has been spent by
    /// the time it is written.
    /// </summary>
    [Fact]
    public async Task A_refused_attempt_never_reaches_the_credential_path()
    {
        var (userId, email) = await SeedProviderAsync();

        using var tight = FactoryWith(new RateLimitPolicies
        {
            LoginPerAccount = new("login-account", 2, TimeSpan.FromMinutes(15), false),
        });

        using var client = ClientWith(tight, SyntheticSourceKey());

        // Two allowed attempts, which DO reach the credential path and are counted there.
        await AttemptAsync(client, email, "wrong-password-here");
        await AttemptAsync(client, email, "wrong-password-here");

        var (failures, count) = await LoginStateAsync(userId);
        var watermark = await AuditWatermarkAsync();

        var refused = await AttemptAsync(client, email, "wrong-password-here");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.Status);

        var (failuresAfter, countAfter) = await LoginStateAsync(userId);

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var newFailureRows = await db.AuditEvents.AsNoTracking()
            .CountAsync(e => e.Id > watermark
                && e.ActorUserId == userId
                && e.EventType == AuditEventType.LoginFailed);

        Assert.True(
            newFailureRows == 0 && countAfter == count,
            $"A refused attempt left {newFailureRows} LoginFailed row(s) and moved "
            + $"AccessFailedCount from {count} to {countAfter}. The refusal is supposed to "
            + "happen before the PBKDF2 hash, the account lookup and the audit write — those "
            + "are the costs an unbounded stream of guesses was buying against a container "
            + "that scales from zero.");

        Assert.Equal(failures, failuresAfter);
    }

    /// <summary>
    /// A request the caller abandons is still counted.
    ///
    /// OTHERWISE THE LIMITER HAS A SWITCH ON IT. Send a guess, drop the socket before the
    /// counter commits, and a limiter holding the request's cancellation token counts
    /// nothing — so an attacker who never waits for an answer is not limited at all. That is
    /// D075's finding about audit rows, on the one control whose entire job is to see a
    /// flood, and it is why <c>IRateLimitStore.ConsumeAsync</c> takes no
    /// <c>CancellationToken</c> to be handed.
    ///
    /// MEASURED THE HARSHEST HONEST WAY: the connection is already aborted before the
    /// pipeline reaches the endpoint, so a write that observes the request's token has no
    /// window at all rather than a narrow one. The count still has to move.
    ///
    /// Control: the <c>ToListAsync(deadline.Token)</c> in SqlRateLimitStore — that is, the
    /// absence of a caller's token on this seam.
    /// Falsified by adding a <c>CancellationToken</c> parameter to
    /// <c>IRateLimitStore.ConsumeAsync</c>, passing <c>context.HttpContext.RequestAborted</c>
    /// from RateLimitFilter and running the statement on it — which is what any call site
    /// does with a token in scope → red, "The counter did not move for a request the caller
    /// abandoned: 0 of 3 attempts were counted." Not slower: ZERO, because a cancelled token
    /// stays cancelled and the command is never issued at all.
    ///
    /// The build had an opinion first, which is worth recording: dropping to the request's
    /// token left the injected deadline unused and the compiler refused it —
    /// "error CS9113: Parameter 'deadline' is unread." Same shape as the note on
    /// <c>AuthenticationTests.Every_identity_store_call_is_bounded_by_the_deadline</c>: the
    /// binding cannot be quietly emptied, only deliberately replaced.
    /// </summary>
    [Fact]
    public async Task A_request_the_caller_abandons_is_still_counted()
    {
        const int attempts = 3;

        var source = SyntheticSourceKey();

        using var abandoned = FactoryWith(
            new RateLimitPolicies(),
            configure: services => services
                .AddSingleton<IStartupFilter, AbortedBeforeTheEndpointFilter>());

        using var client = ClientWith(abandoned, source);

        for (var i = 0; i < attempts; i++)
        {
            try
            {
                await AttemptAsync(client, $"nobody-{Guid.NewGuid():N}@example.com", "guess");
            }
            catch (HttpRequestException)
            {
                // Expected: the caller is gone, so whatever the pipeline answers is
                // unreadable. The question is only what the database ended up holding.
            }
            catch (TaskCanceledException)
            {
                // Same, when the harness tears the response down first.
            }
        }

        var counted = await CountedAsync(new RateLimitPolicies().LoginPerSource, source);

        Assert.True(
            counted == attempts,
            $"The counter did not move for a request the caller abandoned: {counted} of "
            + $"{attempts} attempts were counted. A limiter that observes the caller's "
            + "cancellation has a switch on it — send the guess, drop the socket, and the "
            + "flood is unbounded again (D075's finding, on the control that exists to see "
            + "exactly that traffic).");
    }

    // ------------------------------------------------------------- the set, not a list

    /// <summary>
    /// Every route this application cannot afford to leave open carries a limit — asked of
    /// the route table, not of a list in this file.
    ///
    /// A TEST NAMING THE FOUR ROUTES UNDER <c>/auth</c> STAYS GREEN ON THE FIFTH, which is
    /// this repository's most repeated defect: five guards have shipped holding a hard-coded
    /// set that stopped being complete the day after it was written (D090's sweep,
    /// docs/TEST_STRATEGY.md). So the set comes from <c>EndpointDataSource</c> — the routes
    /// the running application actually mapped.
    ///
    /// AND IT COVERS A PREFIX THAT IS STILL EMPTY. <c>/dictation</c> has no routes today;
    /// WORK_QUEUE 2.5 maps the chunked resumable upload there, and the numbers it has to
    /// respect are already written down on <c>RateLimitPolicies.DictationUpload</c>. This is
    /// what makes that more than a note: the first upload route mapped without a limit
    /// arrives red. A declared policy nothing enforces would be D072's class exactly — a
    /// control described, absent, and reading as stronger than no control at all.
    ///
    /// Control: the <c>.WithMetadata(new RateLimitMetadata("source"))</c> in
    /// <c>RateLimitingExtensions.RateLimitBySource</c>, which is what the route table is
    /// asked for.
    /// Deleted → red, "1 route(s) under a prefix that must be rate limited carry no limit:
    /// POST /auth/mfa/enrol/begin."
    ///
    /// ONE, NOT FIVE, AND THAT IS THE HONEST READING. The other four routes carry
    /// endpoint-level ACCOUNT metadata, so removing the group's source metadata leaves them
    /// still marked as limited — a second control covering for the first. It is the right
    /// answer for what this guard asks, which is "does this route carry a limit at all"
    /// rather than "does it carry both"; the route it does surface is precisely the one with
    /// no account limit of its own, which is the route a deletion would actually leave
    /// unprotected.
    /// </summary>
    [Fact]
    public void Every_expensive_route_carries_a_rate_limit()
    {
        /*
         * The prefixes, not the routes.
         *
         * These two are the whole of what an unauthenticated or an expensive caller can
         * reach and cost money with: the credential path, and the audio upload. The
         * consultation form is deliberately absent — it is limited one tier out, in `web`,
         * and moving it onto this store is what remains of WORK_QUEUE 4.3.
         */
        string[] mustBeLimited = ["/auth", "/dictation"];

        var endpoints = _factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => mustBeLimited.Any(prefix => ("/" + e.RoutePattern.RawText?.TrimStart('/'))
                .StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(
            endpoints.Length > 0,
            $"No route was found under any of {string.Join(", ", mustBeLimited)}. Either "
            + "this application stopped exposing a credential path or this walk stopped "
            + "working — an assertion over an empty set is green and proves nothing "
            + "(docs/TEST_STRATEGY.md).");

        var unlimited = endpoints
            .Where(e => e.Metadata.GetMetadata<RateLimitMetadata>() is null)
            .Select(e => $"{string.Join("/", e.Metadata.GetMetadata<HttpMethodMetadata>()
                ?.HttpMethods ?? ["?"])} {e.RoutePattern.RawText}")
            .ToArray();

        Assert.True(
            unlimited.Length == 0,
            $"{unlimited.Length} route(s) under a prefix that must be rate limited carry no "
            + $"limit: {string.Join(", ", unlimited)}. Every one of these either checks a "
            + "credential without a session or accepts audio, on containers that scale from "
            + "zero — and the five-failure lockout cannot see an address that has no "
            + "account.");
    }

    /// <summary>
    /// The BFF forwards the key this tier partitions by, on the header this tier reads.
    ///
    /// TWO TREES, ONE AGREEMENT, AND A COMMENT CANNOT HOLD IT. The browser never talks to
    /// <c>api</c>, so the only identity available here is one <c>web</c> derived and sent;
    /// if the two files disagree about the header name, every request in production lands in
    /// the shared unattributed bucket and the limiter throttles Michelle and nobody else —
    /// silently, and in the direction that looks like the control working. Same shape as
    /// <c>RequestBoundsTests.The_bff_waits_longer_than_this_api_is_prepared_to_spend</c>,
    /// which is the only kind of test that has ever caught this class here.
    ///
    /// IT ALSO PINS THE DERIVATION, not just the name. <c>web</c> must send the value that
    /// keys the consultation limiter and fills <c>ConsultationRequest.SourceIpHash</c> —
    /// <c>hashClientId(clientIdentifier(...))</c> — rather than a second scheme, because a
    /// second hash correlates with nothing either side ever recorded (D080's one-hash-two-
    /// uses argument, across the hop).
    ///
    /// Control: the <c>CLIENT_KEY_HEADER</c> constant in <c>web/lib/client-key.ts</c>.
    /// Renamed to <c>X-Source-Key</c> → red, "web/lib/client-key.ts does not carry the
    /// header name this API reads (X-Client-Key)."
    ///
    /// Control: the <c>[CLIENT_KEY_HEADER]: await clientKey()</c> in
    /// <c>web/lib/auth/api-client.ts</c>'s <c>post</c>.
    /// Deleted → red, "web/lib/auth/api-client.ts calls fetch 1 time(s) and forwards the
    /// client key 0 time(s)."
    /// </summary>
    [Fact]
    public void The_bff_forwards_the_key_this_api_partitions_by()
    {
        var derivation = File.ReadAllText(RepoTree.File("web/lib/client-key.ts"));

        Assert.True(
            derivation.Contains($"\"{ClientKey.HeaderName}\"", StringComparison.Ordinal),
            $"web/lib/client-key.ts does not carry the header name this API reads "
            + $"({ClientKey.HeaderName}). The browser never reaches this tier, so a request "
            + "arriving without that header has no source at all and falls into the shared "
            + "unattributed bucket — which throttles the BFF, meaning Michelle, and nobody "
            + "else.");

        Assert.True(
            derivation.Contains("hashClientId", StringComparison.Ordinal)
            && derivation.Contains("clientIdentifier", StringComparison.Ordinal),
            "web/lib/client-key.ts no longer derives the key with "
            + "hashClientId(clientIdentifier(...)). A second derivation produces a value "
            + "that correlates with nothing the consultation limiter counted and nothing "
            + "ConsultationRequest.SourceIpHash holds (D080).");

        var caller = File.ReadAllText(RepoTree.File("web/lib/auth/api-client.ts"));

        var fetches = caller.Split("fetch(").Length - 1;
        var forwards = caller.Split("CLIENT_KEY_HEADER]:").Length - 1;

        Assert.True(
            fetches > 0 && forwards == fetches,
            $"web/lib/auth/api-client.ts calls fetch {fetches} time(s) and forwards the "
            + $"client key {forwards} time(s). A call that does not carry it is a login "
            + "attempt this tier cannot attribute to a source.");
    }

    // ---------------------------------------------------------------------- helpers

    /// <summary>What a caller who is timing this endpoint can actually observe.</summary>
    private sealed record Answer(
        HttpStatusCode Status,
        string Body,
        IReadOnlyDictionary<string, string> Headers,
        TimeSpan Elapsed);

    /// <summary>
    /// One password attempt, measured the way an attacker measures it — status line, body
    /// bytes, headers, and the clock stopped once the body has been read.
    ///
    /// <c>Date</c> is dropped because it is a clock rather than a fact about the response,
    /// and it differs between two requests a second apart on every endpoint in the world.
    /// Everything else is compared, because 1.18 F1 was an oracle in three dimensions at
    /// once and a test that read one field stayed green through all of them.
    /// </summary>
    private static async Task<Answer> AttemptAsync(
        HttpClient client, string email, string password)
    {
        var started = Stopwatch.GetTimestamp();

        using var response = await client.PostAsJsonAsync(
            "/auth/password", new PasswordRequest(email, password));

        var body = await response.Content.ReadAsStringAsync();

        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(h => !h.Key.Equals("Date", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);

        return new Answer(
            response.StatusCode, body, headers, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>A source key in the one shape this application produces.</summary>
    private static string SyntheticSourceKey() =>
        Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())
        + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());

    private static HttpClient ClientWith(PracticeApiFactory factory, string sourceKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientKey.HeaderName, sourceKey);
        return client;
    }

    /// <summary>
    /// The application, with the limits a test needs rather than production's.
    ///
    /// Production numbers are chosen so Michelle cannot reach them; a test that had to send
    /// twenty real requests to observe a refusal would spend its time on the password hasher
    /// rather than on the property. Replacing the singleton is how <c>RateLimitPolicies</c>
    /// is designed to be driven — the filter resolves it per request, so this actually takes
    /// effect rather than being read once at route registration.
    /// </summary>
    private PracticeApiFactory FactoryWith(
        RateLimitPolicies policies,
        params IInterceptor[] interceptors) =>
        FactoryWith(policies, configure: null, interceptors);

    private PracticeApiFactory FactoryWith(
        RateLimitPolicies policies,
        Action<IServiceCollection>? configure,
        params IInterceptor[] interceptors) =>
        new(sql.ConnectionString, services =>
        {
            if (interceptors.Length > 0)
            {
                FailureHarness.With(sql.ConnectionString, interceptors)(services);
            }

            services.AddSingleton(policies);
            configure?.Invoke(services);
        });

    /// <summary>Creates a provider with a login. Unique email per call.</summary>
    private async Task<(string UserId, string Email)> SeedProviderAsync()
    {
        var email = $"michelle-{Guid.NewGuid():N}@example.com";

        using var scope = _factory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PracticeUser>>();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var user = new PracticeUser { UserName = email, Email = email };
        var created = await users.CreateAsync(user, Password);
        Assert.True(created.Succeeded, string.Join(", ", created.Errors.Select(e => e.Description)));

        db.Providers.Add(Provider.Create(user.Id, "Michelle", "M.S., CCC-SLP", "SLP-1", "MD"));
        await db.SaveChangesAsync();

        return (user.Id, email);
    }

    /// <summary>The lockout state the credential path maintains, read straight off the row.</summary>
    private async Task<(DateTimeOffset? LockoutEnd, int AccessFailedCount)> LoginStateAsync(
        string userId)
    {
        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        return (user.LockoutEnd, user.AccessFailedCount);
    }

    /// <summary>What the shared store is holding for one partition.</summary>
    private async Task<int> CountedAsync(RateLimitPolicy policy, string sourceKey)
    {
        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var key = RateLimitPartition.KeyFor(policy, "source", sourceKey);

        return await db.Set<RateLimitCounter>().AsNoTracking()
            .Where(c => c.PartitionKey == key)
            .Select(c => c.Count)
            .SingleOrDefaultAsync();
    }

    /// <summary>
    /// The highest audit id at this instant.
    ///
    /// The suite shares one database and a RateLimited row carries no address, so "a row
    /// exists" is true whatever this test does. A watermark is what makes the assertion about
    /// THIS burst.
    /// </summary>
    private async Task<long> AuditWatermarkAsync()
    {
        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        return await db.AuditEvents.AsNoTracking().MaxAsync(e => (long?)e.Id) ?? 0;
    }
}
