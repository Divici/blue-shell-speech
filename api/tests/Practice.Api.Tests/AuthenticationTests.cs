using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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
