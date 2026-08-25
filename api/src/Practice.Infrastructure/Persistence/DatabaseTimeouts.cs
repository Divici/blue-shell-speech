namespace Practice.Infrastructure.Persistence;

/// <summary>
/// How long this application is prepared to wait on the database, in one place — and,
/// more importantly, HOW THE NUMBERS RELATE TO EACH OTHER.
///
/// These numbers were previously nowhere. <c>AddInfrastructure</c> set no command timeout
/// at all, so the bound was ADO.NET's 30-second default — which is not a decision, is not
/// reviewable next to the 180 the design-time factory sets, and can be silently replaced
/// by a <c>Command Timeout</c> keyword in a connection string this application does not
/// own. A default that happens to be reasonable still leaves nobody able to say what the
/// bound is.
///
/// THEN THEY WERE ALL SET SEPARATELY, WHICH IS ITS OWN FAILURE. A thirty-second request
/// timeout was added on top of a retry policy allowed six commands and fifty seconds of
/// backoff, and the two contradicted each other: the retries exist because Azure SQL
/// serverless auto-pauses and the first query of the day fails while it resumes, and the
/// request timeout cancelled exactly that wake-up. Michelle's first request every morning
/// answered 504 where it had previously succeeded after a minute of retrying. A timeout
/// under the budget of the policy it sits on top of does not bound the work; it deletes
/// the recovery.
///
/// So the request bound is DERIVED rather than chosen. <see cref="RetryBudgetFor"/> says
/// how long the retry policy can keep one operation alive; <see cref="RequestTimeoutFor"/>
/// adds one command of grace on top. Change the command timeout or either retry number and
/// the request bound moves with it, and
/// <c>RequestBoundsTests.The_request_bound_outlives_the_retry_budget</c> reads all three
/// off the running application and fails if the relationship is ever broken by hand.
///
/// WHAT THIS CLASS CANNOT PROMISE. It is the bound THIS TIER sets. The tier that gives up
/// first is the one that decides, so the BFF's own timeout has to sit above
/// <see cref="Request"/> — it does, and
/// <c>RequestBoundsTests.The_bff_waits_longer_than_this_api_is_prepared_to_spend</c> reads
/// the constant out of <c>web/lib/api/timeouts.ts</c> and asserts it, because the previous
/// version of this file made a claim about that file in prose and the claim was false.
/// Container Apps ingress imposes a bound of its own that this repository has not measured;
/// it is not described here, because a number nobody has checked is worth less than no
/// number at all (D072).
/// </summary>
public static class DatabaseTimeouts
{
    /// <summary>
    /// The ceiling on ONE command.
    ///
    /// Thirty seconds, which is what SqlClient would have used anyway — the point is that
    /// it is now stated rather than inherited. Lower would start failing legitimate work
    /// against a database that has just resumed from auto-pause; higher would extend a
    /// wait nobody is waiting for.
    ///
    /// NOT the ceiling on an operation, and never was: <see cref="RetryBudget"/> is that.
    /// The audit writer's docstring has to name the budget rather than this number, because
    /// an audit write does not observe the request token and nothing else stops it.
    /// </summary>
    public const int CommandSeconds = 30;

    /// <summary><see cref="CommandSeconds"/>, for the arithmetic below.</summary>
    public static readonly TimeSpan Command = TimeSpan.FromSeconds(CommandSeconds);

    /// <summary>
    /// Retries, and the longest gap between them — the arguments <c>AddInfrastructure</c>
    /// hands <c>EnableRetryOnFailure</c>.
    ///
    /// Named here rather than written into that call because they are half of the request
    /// bound. Passing literals to EF and choosing the request timeout somewhere else is how
    /// the two ended up contradicting each other.
    /// </summary>
    public const int MaxRetryCount = 5;

    /// <inheritdoc cref="MaxRetryCount"/>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The longest a single operation can stay alive inside the retry policy.
    ///
    /// <paramref name="maxRetryCount"/> retries means <c>maxRetryCount + 1</c> attempts,
    /// each of them allowed <paramref name="commandTimeout"/>, with a backoff of up to
    /// <paramref name="maxRetryDelay"/> between consecutive attempts. EF's exponential
    /// backoff is capped by that maximum, so this is the worst case rather than the usual
    /// one — which is the right side to size a timeout from.
    ///
    /// A function, not just a constant, so the relationship can be exercised at a scale a
    /// test can afford to wait for.
    /// </summary>
    public static TimeSpan RetryBudgetFor(
        TimeSpan commandTimeout, int maxRetryCount, TimeSpan maxRetryDelay) =>
        (commandTimeout * (maxRetryCount + 1)) + (maxRetryDelay * maxRetryCount);

    /// <summary>
    /// The request bound that contains <see cref="RetryBudgetFor"/> instead of cutting it
    /// short: the budget, plus one command of grace.
    ///
    /// The grace is for the work either side of the retried operation — resolving the
    /// forwarded provider, serialising a response, and the audit write that deliberately
    /// holds no token (D075) and therefore continues after the request bound has passed.
    /// One command's worth is arbitrary in the way any margin is; what is not arbitrary is
    /// that it is positive, which is the property the test asserts.
    /// </summary>
    public static TimeSpan RequestTimeoutFor(
        TimeSpan commandTimeout, int maxRetryCount, TimeSpan maxRetryDelay) =>
        RetryBudgetFor(commandTimeout, maxRetryCount, maxRetryDelay) + commandTimeout;

    /// <summary>
    /// What the configured policy can spend: 3 minutes 50 seconds.
    ///
    /// Six attempts of thirty seconds, plus five backoffs of up to ten. Quoted in
    /// <see cref="Practice.Infrastructure.Identity.IAuditWriter"/>, which is bounded by
    /// this and by nothing else.
    /// </summary>
    public static readonly TimeSpan RetryBudget =
        RetryBudgetFor(Command, MaxRetryCount, MaxRetryDelay);

    /// <summary>
    /// The ceiling on a whole request, for the work that can be abandoned: 4 minutes
    /// 20 seconds.
    ///
    /// Long, and deliberately so. A request timeout exists to stop a request nobody is
    /// waiting for from holding a pooled connection on a container that scales to zero —
    /// and the request most likely to be slow here is the first one of the day, against a
    /// database resuming from auto-pause, which somebody very much IS waiting for. Killing
    /// that one to protect a connection is the wrong trade in both directions: it costs the
    /// clinician her first request and it saves nothing, because the retry policy would
    /// have finished on its own.
    ///
    /// It bounds only what observes <c>HttpContext.RequestAborted</c>. Audit writes
    /// deliberately do not (D075) — an audit row that disappears when the caller walks
    /// away is not an audit row — so this is a bound on reads and on the transaction body,
    /// never on the record that something was attempted.
    /// </summary>
    public static readonly TimeSpan Request =
        RequestTimeoutFor(Command, MaxRetryCount, MaxRetryDelay);
}
