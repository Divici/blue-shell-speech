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
/// documented design, and it is tracked for slice 9 hardening.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Authentication");

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
        });

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
        });

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
        });

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
        });

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
