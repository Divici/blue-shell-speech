using Practice.Api.RateLimiting;
using Practice.Infrastructure.RateLimiting;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using Practice.Api.Auth;
using Practice.Domain.Auditing;
using Practice.Domain.Providers;
using Practice.Infrastructure.Identity;
using Practice.Infrastructure.Persistence;

namespace Practice.Api.Tests;

/// <summary>
/// The authentication flow, end to end, against real SQL Server.
///
/// The single most important property asserted here: <b>a correct password never produces
/// a session.</b> This account holds every patient record in the practice, so a stolen
/// password must not be sufficient on its own (docs/SECURITY.md).
/// </summary>
[Collection(UsesSqlServer.Name)]
public sealed class AuthenticationTests(SqlServerFixture sql) : IAsyncLifetime, IDisposable
{
    private PracticeApiFactory _factory = null!;
    private HttpClient _client = null!;

    /// <summary>
    /// This test's own source, for the rate limiter's source partition.
    ///
    /// xunit builds a fresh instance of this class per test, so every test gets its own —
    /// which is what stops the class measuring its own leftovers. The suite shares one
    /// database and the limiter's counters are in it, so without this every request from
    /// every test in this file would fall into the ONE shared "unattributed" bucket and the
    /// tests that happened to run last would be answered 429 instead of the behaviour they
    /// assert. That is D068's finding in the E2E suite, one tier along, and it is not a way
    /// of switching the limiter off: behind ingress, different callers genuinely do arrive
    /// with different forwarded keys.
    /// </summary>
    private readonly string _source =
        Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())
        + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());

    public Task InitializeAsync()
    {
        _factory = new PracticeApiFactory(sql.ConnectionString);
        _client = ClientFor(_factory);
        return Task.CompletedTask;
    }

    /// <summary>A client that carries this test's source key, like `web` would.</summary>
    private HttpClient ClientFor(PracticeApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ClientKey.HeaderName, _source);
        return client;
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Both interfaces are implemented: xunit calls DisposeAsync, and CA1001
    /// requires a type owning disposable fields to be disposable in its own right.</summary>
    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private const string Password = "correct-horse-battery-staple";

    /// <summary>Creates a provider with a login. Unique email per call so tests do not collide.</summary>
    private async Task<(string UserId, string Email)> SeedProviderAsync(bool active = true)
    {
        var email = $"michelle-{Guid.NewGuid():N}@example.com";

        using var scope = _factory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PracticeUser>>();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var user = new PracticeUser { UserName = email, Email = email };
        var created = await users.CreateAsync(user, Password);
        Assert.True(created.Succeeded, string.Join(", ", created.Errors.Select(e => e.Description)));

        var provider = Provider.Create(user.Id, "Michelle", "M.S., CCC-SLP", "SLP-1", "MD");
        if (!active) provider.Deactivate();

        db.Providers.Add(provider);
        await db.SaveChangesAsync();

        return (user.Id, email);
    }

    private Task<PasswordResponse> PostPasswordAsync(string email, string password) =>
        PostPasswordAsync(_client, email, password);

    private static async Task<PasswordResponse> PostPasswordAsync(
        HttpClient client, string email, string password)
    {
        using var response = await client.PostAsJsonAsync(
            "/auth/password", new PasswordRequest(email, password));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PasswordResponse>())!;
    }

    // ---------------------------------------------------------------- password

    [Fact]
    public async Task Correct_password_alone_never_returns_a_session()
    {
        var (_, email) = await SeedProviderAsync();

        var result = await PostPasswordAsync(email, Password);

        // Not "ok", not a session — the next required step.
        Assert.Equal("mfa_enrolment_required", result.Status);
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        var (_, email) = await SeedProviderAsync();

        Assert.Equal("invalid", (await PostPasswordAsync(email, "wrong-password-here")).Status);
    }

    /// <summary>
    /// An unknown email and a wrong password must be indistinguishable to the caller.
    /// Anything else turns the login endpoint into an account-enumeration oracle.
    ///
    /// COMPARES THE WHOLE ANSWER — status line and body bytes — rather than two parsed
    /// fields. This test was green throughout the commit that made the two branches answer
    /// 200 and 504, because it read the response through <c>EnsureSuccessStatusCode</c>: a
    /// 504 with an empty body could only surface here as a thrown HttpRequestException,
    /// which is not what "these two are the same" looks like when it fails.
    ///
    /// AND IT STILL CANNOT SEE THE DEFECT IT WAS CITED AGAINST, which is worth writing
    /// down rather than pretending otherwise. The divergence was a cancellation token, and
    /// against a database that answers in a millisecond nothing is ever cancelled — both
    /// branches behave identically here whichever token they hold.
    /// <see cref="Unknown_email_answers_like_a_wrong_password_when_the_request_bound_fires"/>
    /// is the one that can, because it makes the request bound fire.
    ///
    /// Control: the <c>PasswordOutcome.InvalidCredentials</c> the unknown-email branch
    /// returns, which is what makes it collapse into the same response as a wrong password
    /// at <c>AuthEndpoints</c>' <c>_ =></c> arm.
    /// Falsified to <c>PasswordOutcome.LockedOut</c> — a falsification rather than a
    /// deletion, because there is no line to delete: the defect shape here is a branch
    /// answering differently, not a missing guard — → red on the body comparison,
    /// "Assert.Equal() Failure: Strings differ ↓ (pos 11) / Expected:
    /// "{"status":"invalid","userId":null,"lockou"··· / Actual:
    /// "{"status":"locked_out","userId":null,"loc"···". Position 11 is the first byte that
    /// differs, which is what comparing the whole body buys over comparing one parsed field.
    /// </summary>
    [Fact]
    public async Task Unknown_email_is_indistinguishable_from_a_wrong_password()
    {
        var (_, email) = await SeedProviderAsync();

        var unknown = await AttemptAsync(_client, "nobody@example.com", Password);
        var wrong = await AttemptAsync(_client, email, "wrong-password-here");

        Assert.Equal(wrong.Status, unknown.Status);
        Assert.Equal(wrong.Body, unknown.Body);
        Assert.Equal(HttpStatusCode.OK, unknown.Status);

        // The body they agree on has to be the refusal, not a shared error page.
        Assert.Contains("\"invalid\"", unknown.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A disabled provider must not be distinguishable either — "that account is
    /// disabled" confirms the account exists.
    /// </summary>
    [Fact]
    public async Task Inactive_provider_is_indistinguishable_from_invalid_credentials()
    {
        var (_, email) = await SeedProviderAsync(active: false);

        var result = await PostPasswordAsync(email, Password);

        Assert.Equal("invalid", result.Status);
        Assert.Null(result.UserId);
    }

    [Fact]
    public async Task Account_locks_out_after_five_failures()
    {
        var (_, email) = await SeedProviderAsync();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await PostPasswordAsync(email, "wrong-password-here");
        }

        var result = await PostPasswordAsync(email, Password);

        Assert.Equal("locked_out", result.Status);
        Assert.True(result.LockoutSeconds > 0);
    }

    // ---------------------------------------------------------------- MFA

    [Fact]
    public async Task Mfa_enrolment_then_verification_produces_a_session()
    {
        var (userId, email) = await SeedProviderAsync();

        Assert.Equal("mfa_enrolment_required", (await PostPasswordAsync(email, Password)).Status);

        var begin = await _client.PostAsJsonAsync("/auth/mfa/enrol/begin", new UserRequest(userId));
        var enrolment = (await begin.Content.ReadFromJsonAsync<EnrolmentDto>())!;
        Assert.False(string.IsNullOrWhiteSpace(enrolment.SharedKey));
        Assert.Contains("otpauth://totp/", enrolment.AuthenticatorUri, StringComparison.Ordinal);

        var complete = await _client.PostAsJsonAsync(
            "/auth/mfa/enrol/complete", new MfaRequest(userId, Totp(enrolment.SharedKey)));
        var enrolled = (await complete.Content.ReadFromJsonAsync<EnrolmentCompleteDto>())!;

        Assert.True(enrolled.Succeeded);
        Assert.Equal(10, enrolled.RecoveryCodes.Count);

        // With MFA enabled, the password step now demands a code rather than enrolment.
        Assert.Equal("mfa_required", (await PostPasswordAsync(email, Password)).Status);

        var verify = await _client.PostAsJsonAsync(
            "/auth/mfa/verify", new MfaRequest(userId, Totp(enrolment.SharedKey)));
        var session = (await verify.Content.ReadFromJsonAsync<SessionResponse>())!;

        Assert.True(session.Succeeded);
        Assert.Equal("Michelle", session.DisplayName);
        Assert.NotNull(session.ProviderPublicId);
    }

    [Fact]
    public async Task A_wrong_mfa_code_does_not_produce_a_session()
    {
        var (userId, email) = await SeedProviderAsync();
        var enrolment = await EnrolAsync(userId);

        Assert.Equal("mfa_required", (await PostPasswordAsync(email, Password)).Status);

        var verify = await _client.PostAsJsonAsync(
            "/auth/mfa/verify", new MfaRequest(userId, "000000"));
        var session = (await verify.Content.ReadFromJsonAsync<SessionResponse>())!;

        Assert.False(session.Succeeded);
        Assert.Null(session.ProviderPublicId);
        Assert.NotNull(enrolment);
    }

    [Fact]
    public async Task A_recovery_code_works_once_and_only_once()
    {
        var (userId, _) = await SeedProviderAsync();
        var enrolled = await EnrolAsync(userId);
        var code = enrolled.RecoveryCodes[0];

        var first = await _client.PostAsJsonAsync(
            "/auth/mfa/recovery", new MfaRequest(userId, code));
        var firstSession = (await first.Content.ReadFromJsonAsync<SessionResponse>())!;

        Assert.True(firstSession.Succeeded);
        Assert.True(firstSession.UsedRecoveryCode);
        Assert.Equal(9, firstSession.RecoveryCodesRemaining);

        // Replaying it must fail — that is the whole point of "single use".
        var second = await _client.PostAsJsonAsync(
            "/auth/mfa/recovery", new MfaRequest(userId, code));
        var secondSession = (await second.Content.ReadFromJsonAsync<SessionResponse>())!;

        Assert.False(secondSession.Succeeded);
    }

    // ---------------------------------------------------------------- audit

    /// <summary>
    /// Slice 2 acceptance: LoginSucceeded, LoginFailed and MfaChallenged are audited.
    /// </summary>
    [Fact]
    public async Task Authentication_attempts_are_audited()
    {
        var (userId, email) = await SeedProviderAsync();
        await EnrolAsync(userId);

        await PostPasswordAsync(email, "wrong-password-here");
        await PostPasswordAsync(email, Password);

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var events = await db.AuditEvents
            .AsNoTracking()
            .Where(e => e.ActorUserId == userId)
            .Select(e => e.EventType)
            .ToListAsync();

        Assert.Contains(AuditEventType.LoginFailed, events);
        Assert.Contains(AuditEventType.MfaChallenged, events);
        Assert.Contains(AuditEventType.MfaEnrolled, events);
    }

    /// <summary>
    /// The audit log must never contain clinical content — or, here, a credential. A
    /// password reaching the audit table would put it in the one place most likely to be
    /// exported to a SIEM or read by a third party (docs/SECURITY.md).
    /// </summary>
    [Fact]
    public async Task Audit_metadata_never_contains_a_credential()
    {
        var (_, email) = await SeedProviderAsync();
        await PostPasswordAsync(email, Password);
        await PostPasswordAsync(email, "wrong-password-here");

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var metadata = await db.AuditEvents.AsNoTracking()
            .Select(e => e.Metadata).ToListAsync();

        Assert.DoesNotContain(metadata, m => m is not null && m.Contains(Password, StringComparison.Ordinal));
        Assert.DoesNotContain(metadata, m => m is not null && m.Contains(email, StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------- the bounds the login path observes (1.17 F1)

    /*
     * EVERY DATABASE CALL IN ProviderAuthenticator GOES THROUGH UserManager<PracticeUser>,
     * AND NOT ONE OF ITS METHODS TAKES A CancellationToken.
     *
     * FindByEmailAsync, CheckPasswordAsync, AccessFailedAsync, ResetAccessFailedCountAsync,
     * GetTwoFactorEnabledAsync, VerifyTwoFactorTokenAsync, UpdateAsync — none of them has an
     * overload that accepts one. So for as long as the authenticator was written the obvious
     * way, the login path observed NEITHER of this application's two bounds: not
     * HttpContext.RequestAborted, and not the uncancellable-write deadline that
     * DatabaseTimeouts.Ceiling is made of.
     *
     * Both consequences are real and they point in opposite directions:
     *
     *   * a login against a database resuming from auto-pause ran on with no bound at all,
     *     past the request bound and past the ceiling the BFF is sized against;
     *   * and because that unbounded work spent the shared grace, the audit row written
     *     AFTER it found a deadline that had already expired — and a cancelled token stays
     *     cancelled, so the write threw instantly rather than getting a short go. The one
     *     row recording a failed credential attempt was lost precisely when somebody was
     *     attacking the account, which is the only time it matters.
     *
     * Fixing twenty call sites was never the answer. UserManager funnels every store call
     * through one protected CancellationToken property, so overriding it binds all of them
     * at once — including the ones nobody has written yet.
     */

    /// <summary>
    /// The UserManager the application resolves runs its store calls on the deadline.
    ///
    /// ONE ASSERTION FOR EVERY CALL SITE, which is why it is worth having as well as the
    /// measured tests below. The alternative — listing the seven methods the reviewer found
    /// — is a guard over a set that holds a hard-coded list, and stays green the day an
    /// eighth is called (docs/TEST_STRATEGY.md, D090). The property is the only lever, and
    /// the second assertion here is what establishes that: it walks UserManager's own
    /// asynchronous surface and reports how much of it cannot be handed a token, so if a
    /// future Identity ever grows the overloads, this goes red and the design is worth
    /// revisiting rather than silently kept.
    ///
    /// Control: the <c>.AddUserManager&lt;PracticeUserManager&gt;()</c> call in
    /// InfrastructureServices.AddInfrastructure.
    /// Deleted → red, "UserManager`1 runs its store calls on a token that is not the
    /// request scope's UncancellableWriteDeadline, so none of the 82 token-less methods on
    /// UserManager observes any bound at all — not the request timeout, and not
    /// DatabaseTimeouts.Ceiling." That deletion is the realistic silent failure: the class
    /// still exists, the build is green, and Identity resolves the base manager.
    ///
    /// Control: <c>PracticeUserManager.CancellationToken</c> itself, the override the class
    /// exists for.
    /// Neutered to <c>CancellationToken.None</c> — the base's own value — → the BUILD
    /// refuses it: "error CS9113: Parameter 'deadline' is unread." Recorded because it is
    /// the more useful fact: the override cannot be quietly emptied, only the registration
    /// can, which is why the registration is the control this line names first.
    /// </summary>
    [Fact]
    public void Every_identity_store_call_is_bounded_by_the_deadline()
    {
        var tokenless = typeof(UserManager<PracticeUser>)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name.EndsWith("Async", StringComparison.Ordinal))
            .Where(method => !method.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(CancellationToken)))
            .Select(method => method.Name)
            .Distinct()
            .ToArray();

        Assert.True(
            tokenless.Length > 0,
            "UserManager now offers CancellationToken overloads. The whole reason this "
            + "application overrides the protected CancellationToken property is that it "
            + "did not, so revisit PracticeUserManager rather than deleting this test.");

        using var scope = _factory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PracticeUser>>();
        var deadline = scope.ServiceProvider.GetRequiredService<UncancellableWriteDeadline>();

        var bound = (CancellationToken)ProtectedMember.Read(users, "CancellationToken");

        Assert.True(
            bound == deadline.Token,
            $"{users.GetType().Name} runs its store calls on a token that is not the "
            + "request scope's UncancellableWriteDeadline, so none of the "
            + $"{tokenless.Length} token-less methods on UserManager observes any bound at "
            + "all — not the request timeout, and not DatabaseTimeouts.Ceiling.");
    }

    /// <summary>
    /// A wrong password leaves its audit row even when the bookkeeping that follows outlives
    /// the grace.
    ///
    /// THE SHAPE OF THE FAILURE THIS PINS. A phone locks a fifth of a second into a login;
    /// the request bound fires; the grace starts; the failure-count UPDATE then sits against
    /// a database that is refusing work for longer than the grace lasts. Under the code this
    /// test was written against, the LoginFailed row was written after that UPDATE, on a
    /// deadline the UPDATE had already exhausted — and a cancelled token stays cancelled, so
    /// SaveChangesAsync threw before issuing anything and the row was lost.
    ///
    /// The lookup is stalled too, and for a reason: the request bound has to fire BEFORE the
    /// outcome is decided, or the audit write happens on a deadline that is still whole and
    /// the test proves nothing. 1.5s against a 1s bound puts the cancellation inside the
    /// lookup, so the grace is already running by the time there is anything to audit.
    ///
    /// Scaled — 1s and 2s against production's 10m20s and 90s — because the relationship is
    /// what is under test and nothing can be measured against twelve minutes.
    ///
    /// Control: the ORDER of the two writes in VerifyPasswordAsync's bad-password branch —
    /// the <c>audit.WriteAsync</c> call preceding <c>userManager.AccessFailedAsync</c>.
    /// Swapped back, which is how it was written → red after 3s, "Assert.Contains()
    /// Failure: Item not found in collection / Collection: [] / Not found:
    /// "reason=bad-password"". An empty collection, not a late row: the deadline is spent
    /// by the time the write starts and a cancelled token stays cancelled, so the save
    /// throws before issuing anything.
    ///
    /// Deleting <c>.AddUserManager&lt;PracticeUserManager&gt;()</c> leaves this test GREEN,
    /// correctly — an unbounded UPDATE finishes and the audit row lands either way. That is
    /// <see cref="A_login_against_a_wedged_database_stops_at_the_ceiling"/>'s control, and
    /// the two tests exist separately because F1 is two defects wearing one coat: work that
    /// observes no bound, and a row lost because of it.
    /// </summary>
    [Fact]
    public async Task A_failed_login_is_audited_even_when_the_bookkeeping_outlives_the_grace()
    {
        var (userId, email) = await SeedProviderAsync();

        var requestBound = TimeSpan.FromSeconds(1);
        var grace = TimeSpan.FromSeconds(2);

        using var stalled = StalledFactory(
            requestBound,
            grace,
            new StallsEveryStatementMatching("FROM [AspNetUsers]", TimeSpan.FromMilliseconds(1500)),
            new StallsEveryStatementMatching("UPDATE [AspNetUsers]", TimeSpan.FromSeconds(20)));

        using var client = ClientFor(stalled);

        // Warm the host and the pool, so the bound below fires inside the lookup rather
        // than inside everything a first request drags in with it.
        (await client.GetAsync("/health/live")).Dispose();

        using var response = await client.PostAsJsonAsync(
            "/auth/password", new PasswordRequest(email, "wrong-password-here"));
        response.Dispose();

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var reasons = await db.AuditEvents
            .AsNoTracking()
            .Where(e => e.ActorUserId == userId && e.EventType == AuditEventType.LoginFailed)
            .Select(e => e.Metadata)
            .ToListAsync();

        Assert.Contains(
            "reason=bad-password",
            reasons.Where(reason => reason is not null)!,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// A login against a database that will not answer stops at the ceiling, like every
    /// other request.
    ///
    /// The failure-count UPDATE is stalled for twenty seconds and nothing else is, so what
    /// the clock measures is the Identity store call and nothing around it. Both halves of
    /// the claim are in the elapsed time, for the same reason
    /// <c>RequestBoundsTests.The_ceiling_is_the_request_bound_plus_the_uncancellable_tail</c>
    /// asserts both: a response arriving before the request bound would mean there was no
    /// uncancellable work to bound and the test proved nothing, and one arriving after the
    /// ceiling would mean the bound does not hold.
    ///
    /// A NOTE ON WHY THIS IS NOT THE REQUEST'S TOKEN. Handing UserManager
    /// HttpContext.RequestAborted would bound it too, and would also let an attacker skip
    /// the lockout counter entirely: send a password guess, close the socket before
    /// AccessFailedAsync commits, and the five-failure lockout in AddInfrastructure never
    /// counts to five. The failure count is the same category of write as an audit row —
    /// something that already happened, which the caller going away does not un-happen.
    ///
    /// Control: the <c>.AddUserManager&lt;PracticeUserManager&gt;()</c> call in
    /// InfrastructureServices.AddInfrastructure.
    /// Deleted → red after 20 seconds, "The login ran for 20.2s against a 1s request bound
    /// and a 2s grace. UserManager takes no CancellationToken on any of its methods, so
    /// unless the manager the application resolves binds its store calls to the deadline,
    /// the whole login path sits outside both of this tier's bounds and
    /// DatabaseTimeouts.Ceiling is not a ceiling." Twenty seconds is the whole stall: the
    /// bound contributed nothing at all, which is exactly the shape of the finding.
    /// </summary>
    [Fact]
    public async Task A_login_against_a_wedged_database_stops_at_the_ceiling()
    {
        var (_, email) = await SeedProviderAsync();

        var requestBound = TimeSpan.FromSeconds(1);
        var grace = TimeSpan.FromSeconds(2);
        var wedged = TimeSpan.FromSeconds(20);

        using var stalled = StalledFactory(
            requestBound,
            grace,
            new StallsEveryStatementMatching("UPDATE [AspNetUsers]", wedged));

        using var client = ClientFor(stalled);
        (await client.GetAsync("/health/live")).Dispose();

        var started = Stopwatch.GetTimestamp();
        using var response = await client.PostAsJsonAsync(
            "/auth/password", new PasswordRequest(email, "wrong-password-here"));
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.True(
            elapsed > requestBound,
            $"The response arrived in {elapsed.TotalSeconds:0.0}s, within the "
            + $"{requestBound.TotalSeconds:0}s request bound. This test has to reach an "
            + "Identity store call that is still running when that bound fires, or it "
            + "measures nothing — check that the failure-count UPDATE is the statement "
            + "being stalled.");

        // The grace, plus room for the host: the claim is that a bound holds, not that it
        // fires at an exact instant. The alternative it is separating itself from is 20s.
        var ceiling = requestBound + grace + TimeSpan.FromSeconds(3);

        Assert.True(
            elapsed < ceiling,
            $"The login ran for {elapsed.TotalSeconds:0.0}s against a "
            + $"{requestBound.TotalSeconds:0}s request bound and a "
            + $"{grace.TotalSeconds:0}s grace. UserManager takes no CancellationToken on "
            + "any of its methods, so unless the manager the application resolves binds "
            + "its store calls to the deadline, the whole login path sits outside both of "
            + "this tier's bounds and DatabaseTimeouts.Ceiling is not a ceiling.");
    }

    // ------------------- the enumeration oracle, measured (1.18 F1)

    /// <summary>
    /// An unknown email and a wrong password answer with the same status, the same bytes,
    /// and in the same time — MEASURED, against a database slow enough that the request
    /// bound fires while the lookup is still running.
    ///
    /// WHY THE HEALTHY-DATABASE SIBLING ABOVE CANNOT SEE THIS, which is the finding rather
    /// than a footnote to it. <see cref="Unknown_email_is_indistinguishable_from_a_wrong_password"/>
    /// runs against a database that answers in a millisecond, so
    /// <c>HttpContext.RequestAborted</c> is never cancelled and a branch that observes it
    /// behaves exactly like a branch that does not. THE DIVERGENCE ONLY EXISTS AFTER
    /// CANCELLATION. Two further reasons it could not have seen it even then: it went
    /// through <c>EnsureSuccessStatusCode</c>, so a 504 with an empty body surfaces as a
    /// thrown HttpRequestException rather than as the difference it is; and it compared two
    /// parsed fields, never the clock and never the audit table.
    ///
    /// MEASURED AGAINST THE CODE THIS TEST WAS WRITTEN FOR, with every statement against
    /// the users table stalled 1.5s under a 1s request bound: unknown email → 504, empty
    /// body, 1527 ms, and ZERO rows carrying <c>reason=unknown-email</c>; known email with a
    /// wrong password → 200, <c>{"status":"invalid",…}</c>, 4696 ms. Three dimensions and
    /// the audit trail, all four telling an attacker which addresses are real.
    ///
    /// Control: the <c>await bookkeeping.CountFailureAsync(absent.Id)</c> in
    /// <c>ProviderAuthenticator</c>'s unknown-email branch — the round trip that matches the
    /// one the wrong-password branch makes.
    /// Deleted → red, "An unknown email answered in 1561 ms and a wrong password in 3068 ms
    /// — 1506 ms apart, which is more than one 1500 ms round trip against the users table."
    /// Exactly one stall's worth, which is the shape of the claim: the branch that finds
    /// nothing was skipping the write the branch that finds something performs.
    ///
    /// Control: the ABSENCE of <c>ct</c> on that branch's <c>Task.Run</c>.
    /// Restored, which is how it shipped → red on the first assertion, "Assert.Equal()
    /// Failure: Values differ / Expected: OK / Actual: GatewayTimeout". Not a slower branch
    /// — a different status code, because Task.Run refuses to start on a cancelled token
    /// and the throw escapes before the audit write.
    /// </summary>
    [Fact]
    public async Task Unknown_email_answers_like_a_wrong_password_when_the_request_bound_fires()
    {
        var (_, email) = await SeedProviderAsync();

        var requestBound = TimeSpan.FromSeconds(1);
        var grace = TimeSpan.FromSeconds(10);
        var stall = TimeSpan.FromMilliseconds(1500);

        using var stalled = StalledFactory(
            requestBound, grace, new StallsEveryStatementAgainst("AspNetUsers", stall));

        using var client = ClientFor(stalled);

        /*
         * Warmed twice, and both warm-ups are unknown emails.
         *
         * The first request of a process drags in host start-up, the connection pool and
         * the password hasher's first PBKDF2 — none of which either branch pays for again,
         * and this test compares two elapsed times. Unknown rather than wrong-password so
         * the warm-up does not spend one of the five failures the lockout counts.
         */
        (await client.GetAsync("/health/live")).Dispose();
        await AttemptAsync(client, $"warm-{Guid.NewGuid():N}@example.com", Password);

        var watermark = await AuditWatermarkAsync();

        var unknown = await AttemptAsync(
            client, $"nobody-{Guid.NewGuid():N}@example.com", Password);
        var wrong = await AttemptAsync(client, email, "wrong-password-here");

        Assert.Equal(wrong.Status, unknown.Status);
        Assert.Equal(wrong.Body, unknown.Body);

        var gap = (unknown.Elapsed - wrong.Elapsed).Duration();

        Assert.True(
            gap < stall,
            $"An unknown email answered in {unknown.Elapsed.TotalMilliseconds:0} ms and a "
            + $"wrong password in {wrong.Elapsed.TotalMilliseconds:0} ms — {gap.TotalMilliseconds:0} ms "
            + $"apart, which is more than one {stall.TotalMilliseconds:0} ms round trip "
            + "against the users table. A caller who can time the difference can tell "
            + "which addresses have accounts, which is the whole reason the unknown-email "
            + "branch does work at all instead of returning early.");

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var unrecorded = await db.AuditEvents.AsNoTracking()
            .CountAsync(e => e.Id > watermark
                && e.EventType == AuditEventType.LoginFailed
                && e.Metadata == "reason=unknown-email");

        Assert.True(
            unrecorded > 0,
            "A credential attempt against an address with no account left no row in "
            + "AuditEvents. The response is deliberately indistinguishable from every "
            + "other refusal, so nothing else in the system records it — walking an "
            + "address list through this endpoint would leave no evidence at all "
            + "(docs/SECURITY.md §Audit).");
    }

    // ------------------- the lockout under concurrency (1.18 F2)

    /// <summary>
    /// Twenty wrong passwords arriving at once count as twenty, and the account locks.
    ///
    /// MEASURED, and the measurement is the finding: four waves of twenty simultaneous
    /// wrong-password posts — EIGHTY attempts — left <c>AccessFailedCount = 4</c> and
    /// <c>LockoutEnd = NULL</c>. One increment survived per wave. Every request in a wave
    /// read the same row and therefore the same <c>ConcurrencyStamp</c>, which
    /// <c>IdentityDbContext</c> maps as a concurrency token; one UPDATE matched it and the
    /// other nineteen raised <c>DbUpdateConcurrencyException</c>, which
    /// <c>UserStore.UpdateAsync</c> catches and converts into an <c>IdentityResult</c> that
    /// this application then threw away. A five-failure lockout that an N-wide caller can
    /// buy N guesses per count is not a lockout.
    ///
    /// Reads are delayed rather than the whole database being stalled, so the window
    /// between the lookup and the write is wide DELIBERATELY instead of by luck, and the
    /// write itself runs at full speed — the race is what is under test, not a bound. The
    /// application's own request timeout and grace are left exactly as production sets
    /// them for the same reason.
    ///
    /// Asserted in the product's own terms as well as in the row: the next attempt, with
    /// the RIGHT password, has to be refused.
    ///
    /// Control: the single-statement failure count — <c>await CountFailureAsync(user)</c> in
    /// the bad-password branch, reaching <c>LoginBookkeeping</c>'s
    /// <c>CASE WHEN [AccessFailedCount] + 1 >= @max</c> UPDATE.
    /// Replaced with <c>await userManager.AccessFailedAsync(user)</c>, which is how it
    /// shipped → red, "Twenty simultaneous wrong passwords left AccessFailedCount = 1 and
    /// LockoutEnd = NULL, and the CORRECT password was then answered
    /// "mfa_enrolment_required"." ONE increment out of twenty, and the account not locked —
    /// the reviewer's eighty-attempt measurement reproduced at a quarter of the size.
    ///
    /// That same edit also turns
    /// <see cref="No_identity_result_on_the_login_path_is_discarded"/> red, at
    /// ProviderAuthenticator.cs:141. Two tests, two different reasons, and neither covers
    /// for the other: this one would stay green if the count were atomic and its result
    /// thrown away, and that one would stay green if the result were read from a
    /// read-modify-write that loses the race.
    /// </summary>
    [Fact]
    public async Task Twenty_simultaneous_wrong_passwords_lock_the_account()
    {
        var (userId, email) = await SeedProviderAsync();

        using var contended = ContendedFactory(
            new DelaysEveryRead(TimeSpan.FromMilliseconds(250)));

        using var client = ClientFor(contended);
        (await client.GetAsync("/health/live")).Dispose();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            AttemptAsync(client, email, "wrong-password-here")));

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);

        var refused = await PostPasswordAsync(client, email, Password);

        Assert.True(
            refused.Status == "locked_out",
            $"Twenty simultaneous wrong passwords left AccessFailedCount = "
            + $"{user.AccessFailedCount} and LockoutEnd = "
            + $"{user.LockoutEnd?.ToString("O", CultureInfo.InvariantCulture) ?? "NULL"}, "
            + $"and the CORRECT password was then answered \"{refused.Status}\". The "
            + "five-failure lockout in AddInfrastructure is the only thing between this "
            + "account and an offline password list, and a caller who sends its guesses "
            + "concurrently is not being counted.");

        Assert.NotNull(user.LockoutEnd);
    }

    /// <summary>
    /// A wrong password that could not be counted is not answered as an ordinary refusal.
    ///
    /// THE OTHER HALF OF "STOP DISCARDING THE RESULT". Making the count atomic removes the
    /// cause the reviewer measured; it does not make the write infallible, and the version
    /// of this defect that matters is not the concurrency loss but the SILENCE — a login
    /// endpoint that answers "invalid" while its lockout counter sits still looks exactly
    /// like one that works, from the outside and from the logs, forever.
    ///
    /// A 500 with a trace id is a worse experience than "invalid" and the correct answer.
    /// The alternative on offer is a five-failure lockout that never reaches five, in front
    /// of the single account holding every record in the practice.
    ///
    /// AND THE AUDIT ROW IS STILL THERE, which is the ordering on the class doing its job:
    /// the attempt is on file before the bookkeeping is attempted, so the request failing
    /// loses the count and not the evidence (D092).
    ///
    /// Control: the <c>if (!await bookkeeping.CountFailureAsync(...)) throw</c> in
    /// <c>ProviderAuthenticator.CountFailureAsync</c>.
    /// Reduced to a bare <c>await bookkeeping.CountFailureAsync(user.Id);</c> — the discard
    /// this finding is about → red, "The endpoint answered 200 for a credential it refused
    /// without counting." A 200 with <c>{"status":"invalid"}</c>: indistinguishable from a
    /// working lockout, from the caller's side and from the logs.
    /// </summary>
    [Fact]
    public async Task A_wrong_password_that_cannot_be_counted_is_not_refused_quietly()
    {
        var (userId, email) = await SeedProviderAsync();

        using var uncountable = new PracticeApiFactory(
            sql.ConnectionString,
            services => services.AddScoped<ILoginBookkeeping, CountsNothing>());

        using var client = ClientFor(uncountable);

        using var response = await client.PostAsJsonAsync(
            "/auth/password", new PasswordRequest(email, "wrong-password-here"));

        Assert.False(
            response.IsSuccessStatusCode,
            $"The endpoint answered {(int)response.StatusCode} for a credential it refused "
            + "without counting. A refusal that does not reach the lockout counter is a "
            + "guess that cost the caller nothing, and nothing else in the system would "
            + "ever report that the counter had stopped moving.");

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        Assert.True(
            await db.AuditEvents.AsNoTracking().AnyAsync(e =>
                e.ActorUserId == userId
                && e.EventType == AuditEventType.LoginFailed
                && e.Metadata == "reason=bad-password"),
            "The request failed and took the record of the attempt with it. The audit row "
            + "is written before the bookkeeping precisely so that the half which can be "
            + "reconstructed is the half that is lost (D092).");
    }

    /// <summary>
    /// Nothing on the login path throws an <c>IdentityResult</c> away.
    ///
    /// A GUARD OVER THE CLASS, NOT OVER THE SEVEN CALL SITES THAT WERE WRONG. Identity
    /// reports failure by return value, so a discarded result is a silent no-op — and every
    /// one of these read like ordinary code: <c>await userManager.AccessFailedAsync(user);</c>
    /// is what the documentation shows. Seven of them were discarded here, including the
    /// one that decided whether MFA was actually switched on. Listing the seven would be a
    /// guard that holds a hard-coded list and stays green the day an eighth is written,
    /// which is this repository's most repeated defect (D090, docs/TEST_STRATEGY.md).
    ///
    /// So the set comes from REFLECTION over UserManager's own surface, and the file is
    /// read as text: a call is discarded when the statement begins with it. An awaited call
    /// whose value is assigned, tested, or passed as an argument does not begin a
    /// statement, which is exactly the distinction being asserted.
    ///
    /// Control: any one of the checked call sites in <c>ProviderAuthenticator</c> — the
    /// <c>Succeeded(await userManager.SetTwoFactorEnabledAsync(user, true), …)</c> in
    /// CompleteMfaEnrolmentAsync was the one run.
    /// Unwrapped to <c>await userManager.SetTwoFactorEnabledAsync(user, true);</c> → red,
    /// "…Discarded at: ProviderAuthenticator.cs:300." Worth noting that the mutation is
    /// what the ASP.NET Core documentation shows, and the build stays green: this is a
    /// defect with no compiler diagnostic behind it, which is the whole reason for a guard
    /// that reads the file as text.
    /// </summary>
    [Fact]
    public void No_identity_result_on_the_login_path_is_discarded()
    {
        var reporting = typeof(UserManager<PracticeUser>)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.ReturnType == typeof(Task<IdentityResult>))
            .Select(method => method.Name)
            .Distinct()
            .ToArray();

        Assert.True(
            reporting.Length > 0,
            "UserManager no longer reports failure through IdentityResult. This guard reads "
            + "its surface rather than naming methods, so revisit it rather than deleting "
            + "it.");

        var path = RepoTree.File(
            "api/src/Practice.Infrastructure/Identity/ProviderAuthenticator.cs");

        var discarded = File.ReadAllLines(path)
            .Select((text, index) => (Line: index + 1, Text: text.Trim()))
            .Where(line => !line.Text.EndsWith(','))
            .Where(line => reporting.Any(name => line.Text.StartsWith(
                $"await userManager.{name}(", StringComparison.Ordinal)))
            .Select(line => $"ProviderAuthenticator.cs:{line.Line}")
            .ToArray();

        Assert.True(
            discarded.Length == 0,
            "Identity signals failure by return value, so an awaited call whose "
            + "IdentityResult begins and ends a statement is a write that can silently do "
            + "nothing. Discarded at: " + string.Join(", ", discarded)
            + ". That is how a failed SetTwoFactorEnabledAsync came to answer "
            + "\"enrolled\" for an account with no second factor.");
    }

    // ------------------- a success row for a sign-in that never happened (1.18 F3)

    /// <summary>
    /// A sign-in that produced no session is not audited as one.
    ///
    /// MEASURED: with <c>UPDATE [AspNetUsers]</c> stalled twenty seconds under a 1s bound
    /// and a 2s grace, <c>POST /auth/mfa/verify</c> carrying a VALID code answered 504 with
    /// no session — and AuditEvents nevertheless held <c>LoginSucceeded</c>, while
    /// <c>LastMfaAtUtc</c> was still null. The row described a sign-in whose own state
    /// never landed.
    ///
    /// D092'S ASYMMETRY DOES NOT TRANSFER, AND INVERTS. On a FAILURE the audit row is
    /// written first, because the fact is established the moment the credential check
    /// returns and losing the row loses the only evidence the attempt happened. On a
    /// SUCCESS the fact is not established until the writes the session depends on have
    /// landed, so a row written first is a PREDICTION — and <c>LoginSucceeded</c> is the
    /// row an investigator uses to decide which sessions a breach has to be scoped to. A
    /// missing success row can be reconstructed from what a session leaves behind: the very
    /// next request carries the provider context and every read of a record writes
    /// PatientViewed with the actor on it. A false one is not falsifiable by anything.
    ///
    /// The mechanism is ordering, as it is on the failure paths, pointing the other way:
    /// the row is the LAST write before the result is returned, so "the row exists" and
    /// "the caller was told it succeeded" fail together.
    ///
    /// The password step runs on the unstalled client on purpose — it is setup, not the
    /// subject, and its own bookkeeping would spend the grace before the verify was
    /// reached.
    ///
    /// Control: the POSITION of the <c>LoginSucceeded</c> write in
    /// <c>ProviderAuthenticator.CompleteSignInAsync</c> — after the bookkeeping, not before.
    /// Moved back above <c>ClearFailuresAsync</c>, which is how it shipped → red, "1
    /// LoginSucceeded row(s) exist for a request that answered 504 with no session, and
    /// LastMfaAtUtc is null." The row is not merely early: it is the only trace of the
    /// event, and it says the opposite of what happened. Note the deletion is a MOVE — the
    /// write is still there and still on the deadline, which is why nothing else in the
    /// suite notices.
    /// </summary>
    [Fact]
    public async Task A_sign_in_that_never_completed_is_not_audited_as_a_success()
    {
        var (userId, email) = await SeedProviderAsync();
        var enrolled = await EnrolAsync(userId);

        Assert.Equal("mfa_required", (await PostPasswordAsync(email, Password)).Status);

        using var stalled = StalledFactory(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            new StallsEveryStatementMatching(
                "UPDATE [AspNetUsers]", TimeSpan.FromSeconds(20)));

        using var client = ClientFor(stalled);
        (await client.GetAsync("/health/live")).Dispose();

        using var verify = await client.PostAsJsonAsync(
            "/auth/mfa/verify", new MfaRequest(userId, Totp(enrolled.SharedKey)));

        var session = verify.IsSuccessStatusCode
            ? await verify.Content.ReadFromJsonAsync<SessionResponse>()
            : null;

        // The premise. If this ever produces a session the test proves nothing, and the
        // stall is no longer reaching the sign-in's own bookkeeping.
        Assert.True(
            session?.Succeeded is not true,
            "The sign-in completed despite its bookkeeping being stalled far past the "
            + "grace, so this test is no longer measuring the window the finding names.");

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var claimed = await db.AuditEvents.AsNoTracking()
            .CountAsync(e => e.ActorUserId == userId
                && e.EventType == AuditEventType.LoginSucceeded);

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);

        Assert.True(
            claimed == 0,
            $"{claimed} LoginSucceeded row(s) exist for a request that answered "
            + $"{(int)verify.StatusCode} with no session, and LastMfaAtUtc is "
            + $"{user.LastMfaAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "null"}. "
            + "That row is what an investigator scopes a breach with, so a sign-in it "
            + "describes has to have happened.");

        Assert.Null(user.LastMfaAtUtc);
    }

    /// <summary>
    /// The application's own pipeline, scaled down, with named statements made to hang.
    ///
    /// The backstop is deliberately far away (60s), so that the deadline arriving on time is
    /// attributable to ProviderContextMiddleware's binding rather than to the fallback the
    /// deadline uses when nothing binds it — the same reason
    /// <c>FailureHarness.BoundedBy</c> takes the two arguments separately.
    /// </summary>
    private PracticeApiFactory StalledFactory(
        TimeSpan requestBound, TimeSpan grace, params IInterceptor[] interceptors) =>
        new(sql.ConnectionString, services =>
        {
            FailureHarness.With(sql.ConnectionString, interceptors)(services);
            FailureHarness.BoundedBy(services, backstop: TimeSpan.FromSeconds(60), grace);

            services.Configure<RequestTimeoutOptions>(options =>
                options.DefaultPolicy = new RequestTimeoutPolicy { Timeout = requestBound });
        });

    /// <summary>
    /// The application's own bounds, with named statements slowed down.
    ///
    /// Unlike <see cref="StalledFactory"/> the request timeout and the deadline are left
    /// exactly as production sets them. What the caller of this wants is a RACE, and
    /// scaling the bounds down would cut the race short rather than widen it.
    ///
    /// THE RATE LIMITER IS RAISED OUT OF THE WAY, AND THAT IS DELIBERATE RATHER THAN
    /// CONVENIENT. The only test using this sends twenty simultaneous wrong passwords at one
    /// address to prove the LOCKOUT counts them — a different control, in front of which
    /// production's limiter would refuse the eleventh and leave the lockout untested while
    /// the suite stayed green. Two controls in series can only be measured one at a time, so
    /// the limiter has its own file (<see cref="RateLimitTests"/>) and this leaves it wide.
    /// Note what is NOT raised: the lockout threshold, the request bound and the grace are
    /// all production's.
    /// </summary>
    private PracticeApiFactory ContendedFactory(params IInterceptor[] interceptors) =>
        new(sql.ConnectionString, services =>
        {
            FailureHarness.With(sql.ConnectionString, interceptors)(services);

            services.AddSingleton(new RateLimitPolicies
            {
                LoginPerSource = new("login-source", 10_000, TimeSpan.FromMinutes(5), false),
                LoginPerAccount = new("login-account", 10_000, TimeSpan.FromMinutes(15), false),
            });
        });

    // ---------------------------------------------------------------- helpers

    /// <summary>What a caller who is timing this endpoint can actually observe.</summary>
    private sealed record Answer(HttpStatusCode Status, string Body, TimeSpan Elapsed);

    /// <summary>
    /// One password attempt, measured the way an attacker measures it — status line, body
    /// bytes, and the clock stopped once the body has been read rather than when the
    /// headers arrive.
    /// </summary>
    private static async Task<Answer> AttemptAsync(
        HttpClient client, string email, string password)
    {
        var started = Stopwatch.GetTimestamp();

        using var response = await client.PostAsJsonAsync(
            "/auth/password", new PasswordRequest(email, password));

        var body = await response.Content.ReadAsStringAsync();

        return new Answer(response.StatusCode, body, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>
    /// The highest audit id at this instant.
    ///
    /// The suite shares one database and the metadata on a failed login deliberately
    /// carries no address, so "a row saying reason=unknown-email exists" is true whatever
    /// this test does. A watermark is what makes the assertion about THIS attempt.
    /// </summary>
    private async Task<long> AuditWatermarkAsync()
    {
        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        return await db.AuditEvents.AsNoTracking().MaxAsync(e => (long?)e.Id) ?? 0;
    }

    private async Task<Enrolled> EnrolAsync(string userId)
    {
        var begin = await _client.PostAsJsonAsync("/auth/mfa/enrol/begin", new UserRequest(userId));
        var enrolment = (await begin.Content.ReadFromJsonAsync<EnrolmentDto>())!;

        var complete = await _client.PostAsJsonAsync(
            "/auth/mfa/enrol/complete", new MfaRequest(userId, Totp(enrolment.SharedKey)));

        var result = (await complete.Content.ReadFromJsonAsync<EnrolmentCompleteDto>())!;
        Assert.True(result.Succeeded);
        return new Enrolled(enrolment.SharedKey, result.RecoveryCodes);
    }

    /// <summary>The shared key AND the codes: a test that has to produce a valid TOTP
    /// after enrolment needs the first, and one testing single-use needs the second.</summary>
    private sealed record Enrolled(string SharedKey, List<string> RecoveryCodes);

    /// <summary>Generates the code an authenticator app would show for this shared key.</summary>
    private static string Totp(string base32Key) =>
        new Totp(Base32Encoding.ToBytes(base32Key)).ComputeTotp();

    private sealed record EnrolmentDto(string SharedKey, string AuthenticatorUri);

    private sealed record EnrolmentCompleteDto(bool Succeeded, List<string> RecoveryCodes);
}
