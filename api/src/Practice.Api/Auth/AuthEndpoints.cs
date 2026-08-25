using Practice.Api.RateLimiting;
using Practice.Application.Authentication;

namespace Practice.Api.Auth;

/// <summary>
/// Authentication endpoints.
///
/// These are reached ONLY by the Next.js BFF, server-to-server. The API has internal
/// ingress and no public route (docs/ARCHITECTURE.md), so no browser ever calls them
/// directly and none of them sets a cookie — `web` owns the session.
///
/// KNOWN GAP: caller identity between web and api is not yet verified.
/// docs/THREAT_MODEL.md boundary 2 specifies managed identity with a validated token
/// audience. Today the control is network isolation alone: only apps inside the Container
/// Apps environment can reach internal ingress. That is meaningful but weaker than the
/// documented design, and it is tracked for slice 9 hardening — WORK_QUEUE 4.4.
///
/// THAT GAP IS NOW LOAD-BEARING IN A SECOND PLACE, which is worth saying here rather than
/// only where the limiter is written. The rate limit below partitions by a source key
/// <c>web</c> derives and forwards on a header, so anything that can reach this tier
/// directly can choose its own source bucket. It cannot choose its ACCOUNT bucket — that one
/// is derived from what it submits — which is the second reason both dimensions exist rather
/// than only the cheaper one.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        /*
         * LIMITED AT THE GROUP, SO A ROUTE ADDED HERE TOMORROW IS LIMITED TOO.
         *
         * Every route below either checks a credential or hands out an enrolment secret,
         * and every one of them is reachable without a session — this is the tier's whole
         * unauthenticated surface apart from the consultation form and the health probes.
         * Attaching the source limit per route would be a list, and a list is only complete
         * on the day somebody writes it (docs/TEST_STRATEGY.md; D090 five times).
         *
         * THE FIVE-FAILURE LOCKOUT IS NOT A SUBSTITUTE AND THAT IS WHY THIS IS HERE. It can
         * only count attempts against an account that EXISTS, because an unknown address has
         * no row to increment. A stream of guesses at random addresses was therefore bounded
         * by nothing at all in this tier, and each one woke a container that scales from
         * zero, ran a PBKDF2 hash and inserted an audit row (WORK_QUEUE 1.19, D097's closing
         * note, D098).
         */
        var group = app.MapGroup("/auth")
            .WithTags("Authentication")
            .RateLimitBySource(policies => policies.LoginPerSource);

        /*
         * Step one. A correct password does NOT sign anyone in — it returns the next
         * required step. There is no code path from here to a session.
         */
        group.MapPost("/password", async (
            PasswordRequest request,
            IProviderAuthenticator authenticator,
            CancellationToken ct) =>
        {
            var result = await authenticator.VerifyPasswordAsync(
                request.Email, request.Password, ct);

            return result.Outcome switch
            {
                PasswordOutcome.RequiresMfa =>
                    Results.Ok(new PasswordResponse("mfa_required", result.UserId)),

                PasswordOutcome.RequiresMfaEnrolment =>
                    Results.Ok(new PasswordResponse("mfa_enrolment_required", result.UserId)),

                PasswordOutcome.LockedOut =>
                    Results.Ok(new PasswordResponse(
                        "locked_out",
                        null,
                        LockoutSeconds: (int?)result.LockoutRemaining?.TotalSeconds)),

                /*
                 * Inactive and InvalidCredentials collapse to the same response.
                 *
                 * Telling the caller "that account is disabled" confirms the account
                 * exists. The distinction is preserved in the audit log, which is where
                 * it is useful and where an attacker cannot read it.
                 */
                _ => Results.Ok(new PasswordResponse("invalid", null)),
            };
        })
        /*
         * THE SUBMITTED ADDRESS, NOT AN ACCOUNT THAT WAS FOUND.
         *
         * This is the dimension the lockout cannot have. `AccessFailedCount` lives on a row,
         * so an address with no row is counted by nothing however many times it is tried;
         * hashing whatever was typed gives the unknown branch a bucket of exactly the same
         * shape as the known one. That sameness is not a side effect — it is the property
         * that stops this control becoming a fresh enumeration oracle, which is the failure
         * 1.18 F1 measured in status, body, time and the audit trail at once.
         */
        .RateLimitByAccount<PasswordRequest>(
            policies => policies.LoginPerAccount, request => request.Email);

        // Step two: the authenticator code.
        group.MapPost("/mfa/verify", async (
            MfaRequest request,
            IProviderAuthenticator authenticator,
            CancellationToken ct) =>
        {
            var result = await authenticator.VerifyMfaAsync(request.UserId, request.Code, ct);

            return result.Succeeded
                ? Results.Ok(SessionResponse.From(result))
                : Results.Ok(new SessionResponse(false, null, null, null, false, 0));
        })
        /*
         * Six digits that change every thirty seconds is a million-wide keyspace with a
         * short life, and the lockout counts a wrong code — but only for the fifteen minutes
         * it lasts, and only against an account that exists. The account partition here is
         * the user id, which reaches this endpoint from `web`'s encrypted pending-MFA
         * cookie rather than from a form, so it is not a value an attacker picks freely.
         */
        .RateLimitByAccount<MfaRequest>(
            policies => policies.LoginPerAccount, request => request.UserId);

        // Step two, alternative: a single-use recovery code.
        group.MapPost("/mfa/recovery", async (
            MfaRequest request,
            IProviderAuthenticator authenticator,
            CancellationToken ct) =>
        {
            var result = await authenticator.RedeemRecoveryCodeAsync(
                request.UserId, request.Code, ct);

            return result.Succeeded
                ? Results.Ok(SessionResponse.From(result))
                : Results.Ok(new SessionResponse(false, null, null, null, false, 0));
        })
        // Recovery codes are credentials equivalent to the second factor, and there are ten
        // of them for the life of the enrolment. They get the same account bound as a TOTP.
        .RateLimitByAccount<MfaRequest>(
            policies => policies.LoginPerAccount, request => request.UserId);

        // Enrolment: generates the shared key and the otpauth URI for the QR code.
        group.MapPost("/mfa/enrol/begin", async (
            UserRequest request,
            IProviderAuthenticator authenticator,
            CancellationToken ct) =>
        {
            var enrolment = await authenticator.BeginMfaEnrolmentAsync(request.UserId, ct);
            return Results.Ok(enrolment);
        });

        /*
         * Enrolment confirmation. Returns the recovery codes ONCE.
         *
         * They are stored hashed and cannot be retrieved again — which is correct, and
         * must be unmistakable in the UI. These are credentials equivalent to the second
         * factor itself.
         */
        group.MapPost("/mfa/enrol/complete", async (
            MfaRequest request,
            IProviderAuthenticator authenticator,
            CancellationToken ct) =>
        {
            var result = await authenticator.CompleteMfaEnrolmentAsync(
                request.UserId, request.Code, ct);

            return Results.Ok(result);
        })
        // Enrolment confirmation verifies a TOTP too, and a wrong code here is NOT counted
        // by the lockout at all — CompleteMfaEnrolmentAsync returns false and writes nothing.
        // So this is the one credential check on the group with no other bound on it.
        .RateLimitByAccount<MfaRequest>(
            policies => policies.LoginPerAccount, request => request.UserId);

        return app;
    }
}

public sealed record PasswordRequest(string Email, string Password);

public sealed record MfaRequest(string UserId, string Code);

public sealed record UserRequest(string UserId);

/// <summary>
/// The next step, never a session.
///
/// `status` is a closed vocabulary the BFF switches on: mfa_required,
/// mfa_enrolment_required, locked_out, invalid.
/// </summary>
public sealed record PasswordResponse(
    string Status,
    string? UserId,
    int? LockoutSeconds = null);

public sealed record SessionResponse(
    bool Succeeded,
    string? UserId,
    Guid? ProviderPublicId,
    string? DisplayName,
    bool UsedRecoveryCode,
    int RecoveryCodesRemaining)
{
    public static SessionResponse From(MfaResult result) => new(
        result.Succeeded,
        result.UserId,
        result.ProviderPublicId,
        result.DisplayName,
        result.UsedRecoveryCode,
        result.RecoveryCodesRemaining);
}
