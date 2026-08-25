using System.Globalization;
using Practice.Domain.Auditing;
using Practice.Infrastructure.Identity;
using Practice.Infrastructure.RateLimiting;

namespace Practice.Api.RateLimiting;

/// <summary>
/// The source key <c>web</c> forwards, and the header it arrives on.
///
/// THE BROWSER NEVER TALKS TO THIS TIER, which is the whole difficulty. Every request here
/// arrives from the BFF over the internal network, so the socket's remote address is
/// <c>web</c>'s and a limiter keyed on it would limit the BFF — one bucket for the entire
/// internet, which throttles Michelle and nobody else. <c>X-Forwarded-For</c> does not help
/// either: it is not set on this hop, and if it were it would be a header a caller writes.
///
/// So the identity is derived ONE tier out, where the proxy's own observation is available,
/// and forwarded. <c>web/lib/client-key.ts</c> does the derivation —
/// <c>hashClientId(clientIdentifier(x-forwarded-for))</c>, reading the entry Container Apps
/// ingress APPENDED rather than the leading entry the caller chose (D080) — and it is the
/// SAME function that keys the consultation limiter and fills
/// <c>ConsultationRequest.SourceIpHash</c>. One derivation, three uses: a second one would
/// produce values that correlate with nothing either of the others ever recorded.
///
/// <c>RateLimitPartition.SourceKey</c> is what decides whether to believe what arrives, and
/// says there what the trust rests on and what task replaces it (4.4).
/// </summary>
public static class ClientKey
{
    /// <summary>
    /// Kept in step with <c>CLIENT_KEY_HEADER</c> in <c>web/lib/client-key.ts</c>, and
    /// asserted across the two trees by
    /// <c>RateLimitTests.The_bff_forwards_the_key_this_api_partitions_by</c> — a comment
    /// claiming agreement between two repositories' worth of code is D072's defect class,
    /// so the agreement is a test.
    /// </summary>
    public const string HeaderName = "X-Client-Key";

    /// <summary>The forwarded key, validated, or the shared unattributed bucket.</summary>
    public static string From(HttpContext context) =>
        RateLimitPartition.SourceKey(context.Request.Headers[HeaderName].ToString());
}

/// <summary>
/// Marks an endpoint as rate limited, so the route table can be asked rather than trusted.
///
/// <c>RateLimitTests.Every_expensive_route_carries_a_rate_limit</c> walks
/// <c>EndpointDataSource</c> and requires this on every route under the prefixes that are
/// too expensive to leave open. That is the "derive the set from the thing itself" rule
/// (docs/TEST_STRATEGY.md): a test naming the four routes <c>/auth</c> has today stays green
/// on the fifth, and stays green on the whole of <c>/dictation</c> when WORK_QUEUE 2.5 maps
/// it. This makes the first unlimited route arrive red instead.
/// </summary>
/// <param name="Kind">
/// <c>source</c> or <c>account</c> — the dimension this filter partitions by. Also what the
/// audit row's metadata carries, so the two cannot drift.
/// </param>
public sealed record RateLimitMetadata(string Kind);

/// <summary>
/// Counts one request, and refuses it identically to every other refusal if it is over.
///
/// RUNS BEFORE THE ENDPOINT DOES ANY WORK, which is the point of it being a filter rather
/// than a check inside a handler. A refused login must not reach the PBKDF2 hash, the
/// account lookup or the audit write the credential path performs — those are the costs an
/// unbounded stream of guesses was buying against a container that scales from zero, and a
/// limiter that runs after them limits nothing that matters.
///
/// THE 429 IS THE SAME 429 EVERY TIME. Same status, same empty body, same headers, for a
/// source refusal and an account refusal, for an address with an account and one without.
/// The login path was measured as an enumeration oracle in status, body, time AND the audit
/// trail one commit ago (1.18 F1); adding a refusal that varies with the account would put
/// the oracle back somewhere new, which is why <c>RateLimitPolicy.AnnouncesRetryAfter</c> is
/// false for both login policies and why nothing here writes a reason into the response.
///
/// WHAT IS STILL OBSERVABLE, since claiming otherwise is how this file would end up lying:
/// an account refusal costs one more round trip than a source refusal, because the source
/// bucket is consumed first. Which of the two trips is decided by the CALLER'S OWN traffic
/// and not by anything about the account, so it discloses nothing an attacker did not
/// already know about their own requests.
/// </summary>
internal sealed class RateLimitFilter(
    Func<RateLimitPolicies, RateLimitPolicy> policySelector,
    string kind,
    Func<EndpointFilterInvocationContext, string> partitionValue) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var services = context.HttpContext.RequestServices;

        var policy = policySelector(services.GetRequiredService<RateLimitPolicies>());
        var store = services.GetRequiredService<IRateLimitStore>();

        var decision = await store.ConsumeAsync(
            policy, RateLimitPartition.KeyFor(policy, kind, partitionValue(context)));

        if (decision.Allowed) return await next(context);

        /*
         * THE ROW GOES HERE, AND COULD NOT HAVE GONE ANY EARLIER (D097).
         *
         * The rule that ordering follows on every other path in this application is that an
         * audit row is written at the earliest point at which the fact it asserts is already
         * true, and not before. The fact here is "this request was refused by the limiter",
         * and that is not established until the statement above returns — a row written
         * before it would be a prediction about a count the database had not yet taken, and
         * a prediction in AuditEvents is what put a LoginSucceeded row against a sign-in
         * that never happened.
         *
         * It is also written BEFORE the response, and on an uncancellable write, so a caller
         * that fires and drops the connection still leaves the evidence. That is the whole
         * difference between a limiter and a limiter somebody can see firing.
         *
         * ONCE PER PARTITION PER WINDOW — see IRateLimitStore.
         */
        if (decision.CrossedTheLimit)
        {
            await services.GetRequiredService<IAuditWriter>().WriteAsync(AuditEvent.Record(
                AuditEventType.RateLimited,
                AuditOutcome.Denied,
                /*
                 * The SOURCE hash, on both dimensions, and deliberately nothing else.
                 *
                 * It is the value ConsultationRequest.SourceIpHash holds, so "did these
                 * attempts come from the same place as that enquiry" is answerable — which
                 * is the only question a hashed address has ever been kept here to answer.
                 * The submitted address is NOT recorded even when it is the dimension that
                 * tripped: a list of the addresses somebody guessed is the enumeration list
                 * this control exists to deny, and AuditEvents is the table most likely to
                 * be exported (docs/SECURITY.md §Audit). Attempts against an address that
                 * DOES have an account already carry the actor on their LoginFailed rows;
                 * attempts against one that does not have no identity to carry, by design.
                 */
                ipAddress: ClientKey.From(context.HttpContext),
                metadata: string.Create(
                    CultureInfo.InvariantCulture,
                    $"policy={policy.Name};partition={kind};limit={policy.Limit}")));
        }

        if (policy.AnnouncesRetryAfter)
        {
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(
                decision.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        // No body. Nothing to say that is not either useless to a legitimate caller or
        // useful to a hostile one.
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }
}

/// <summary>Attaches the limiter to a group or a route.</summary>
public static class RateLimitingExtensions
{
    /// <summary>
    /// Limits by the forwarded source key. Applied to a GROUP, so it covers every route in
    /// it including ones added later — the reason it is here rather than repeated per route.
    /// </summary>
    public static TBuilder RateLimitBySource<TBuilder>(
        this TBuilder builder, Func<RateLimitPolicies, RateLimitPolicy> policy)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(
            new RateLimitFilter(policy, "source", ctx => ClientKey.From(ctx.HttpContext)));

        return builder.WithMetadata(new RateLimitMetadata("source"));
    }

    /// <summary>
    /// Limits by the identity the caller SUBMITTED — not by one this application has looked
    /// up and found.
    ///
    /// That distinction is the task. A limit keyed on an account row can only count attempts
    /// against accounts that exist, which is precisely the hole the five-failure lockout
    /// left: guesses at addresses nobody has registered increment nothing, because there is
    /// nothing to increment. Keying on the submitted value gives every attempt a bucket and,
    /// just as importantly, gives the known and unknown branches the same one.
    ///
    /// The value is read out of the bound argument, so it costs no second parse of the body
    /// and cannot disagree with what the handler will see.
    /// </summary>
    public static RouteHandlerBuilder RateLimitByAccount<TRequest>(
        this RouteHandlerBuilder builder,
        Func<RateLimitPolicies, RateLimitPolicy> policy,
        Func<TRequest, string?> submitted)
    {
        builder.AddEndpointFilter(new RateLimitFilter(
            policy,
            "account",
            ctx => RateLimitPartition.AccountKey(
                ctx.Arguments.OfType<TRequest>().Select(submitted).FirstOrDefault())));

        return builder.WithMetadata(new RateLimitMetadata("account"));
    }
}
