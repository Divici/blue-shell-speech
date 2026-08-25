using System.Diagnostics;
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

    public Task InitializeAsync()
    {
        _factory = new PracticeApiFactory(sql.ConnectionString);
        _client = _factory.CreateClient();
        return Task.CompletedTask;
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

    private async Task<PasswordResponse> PostPasswordAsync(string email, string password)
    {
        var response = await _client.PostAsJsonAsync(
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
    /// </summary>
    [Fact]
    public async Task Unknown_email_is_indistinguishable_from_a_wrong_password()
    {
        var (_, email) = await SeedProviderAsync();

        var unknown = await PostPasswordAsync("nobody@example.com", Password);
        var wrong = await PostPasswordAsync(email, "wrong-password-here");

        Assert.Equal(wrong.Status, unknown.Status);
        Assert.Null(unknown.UserId);
        Assert.Null(wrong.UserId);
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

        using var client = stalled.CreateClient();

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

        using var client = stalled.CreateClient();
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

    // ---------------------------------------------------------------- helpers

    private async Task<EnrolmentCompleteDto> EnrolAsync(string userId)
    {
        var begin = await _client.PostAsJsonAsync("/auth/mfa/enrol/begin", new UserRequest(userId));
        var enrolment = (await begin.Content.ReadFromJsonAsync<EnrolmentDto>())!;

        var complete = await _client.PostAsJsonAsync(
            "/auth/mfa/enrol/complete", new MfaRequest(userId, Totp(enrolment.SharedKey)));

        var result = (await complete.Content.ReadFromJsonAsync<EnrolmentCompleteDto>())!;
        Assert.True(result.Succeeded);
        return result;
    }

    /// <summary>Generates the code an authenticator app would show for this shared key.</summary>
    private static string Totp(string base32Key) =>
        new Totp(Base32Encoding.ToBytes(base32Key)).ComputeTotp();

    private sealed record EnrolmentDto(string SharedKey, string AuthenticatorUri);

    private sealed record EnrolmentCompleteDto(bool Succeeded, List<string> RecoveryCodes);
}
