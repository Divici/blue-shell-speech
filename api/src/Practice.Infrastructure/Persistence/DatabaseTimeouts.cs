namespace Practice.Infrastructure.Persistence;

/// <summary>
/// How long this application is prepared to wait on the database, in one place — and,
/// more importantly, HOW THE NUMBERS RELATE TO EACH OTHER.
///
/// These numbers were previously nowhere. <c>AddInfrastructure</c> set no command timeout
/// at all, so the bound was ADO.NET's 30-second default — which is not a decision, is not
/// reviewable next to the 180 the design-time factory sets, and can be silently replaced
/// by a <c>Command Timeout</c> keyword in a connection string this application does not
/// own.
///
/// Then they were all set separately, and contradicted each other: a thirty-second request
/// timeout on top of a retry policy allowed six commands and fifty seconds of backoff, so
/// the middleware cancelled the auto-pause wake-up the retries exist to carry. That is
/// what <see cref="RequestTimeoutFor"/> deriving the request bound from the retry budget
/// fixed.
///
/// AND THEN THE DERIVATION ITSELF WAS WRONG, TWICE, IN THE SAME PLACE. Both errors are
/// worth stating because both looked right and both shipped with a test that could not
/// detect them:
///
///   1. <see cref="RetryBudgetFor"/> modelled ONE command per attempt. One attempt of the
///      discard transaction issues <see cref="DiscardCommandsPerAttempt"/> of them, so the
///      budget was short by a factor of three and the request bound cancelled retries it
///      claimed to contain. The count is no longer assumed:
///      <c>RequestBoundsTests.The_discard_issues_the_commands_the_budget_models</c> counts
///      the commands one attempt actually executes, against the running application, and
///      fails when the body grows a fourth.
///   2. The request bound was described as the ceiling on a request. It is not, and cannot
///      be. RequestTimeoutsMiddleware cancels <c>HttpContext.RequestAborted</c> and then
///      AWAITS the pipeline, so it bounds work that observes a token — and audit writes
///      deliberately observe none (D075). The real ceiling was the request bound plus an
///      uncancellable tail nothing bounded. <see cref="Ceiling"/> is that ceiling, and
///      <see cref="UncancellableWriteDeadline"/> is what makes it true rather than
///      arithmetic about it.
///
/// THE ORDER THAT MUST HOLD, and the reason every one of these is derived rather than
/// chosen:
///
///     RetryBudget  &lt;  Request  &lt;  Ceiling  &lt;  the BFF's own timeout
///
/// The tier that gives up first is the tier that decides, so any inversion silently
/// replaces every number below it. <c>RequestBoundsTests</c> reads all four off the
/// running application — the last of them out of <c>web/lib/api/timeouts.ts</c>, because a
/// claim about another tree in a comment is exactly what failed here the first time.
///
/// WHAT THIS CLASS STILL CANNOT PROMISE. A transaction's BEGIN and COMMIT are round trips
/// on an open connection rather than <c>DbCommand</c>s, so no command timeout applies to
/// them and this application sets no bound on either; SqlClient's connection semantics are
/// the whole of it. Container Apps ingress imposes a bound of its own that this repository
/// has not measured. Neither is described as a number here, because a number nobody has
/// checked reads as a decision and is not one (D072).
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
    /// Not the ceiling on a request either: <see cref="Ceiling"/> is that.
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
    /// How many database commands ONE attempt of the longest retried operation issues.
    ///
    /// The longest retried operation in this API is the discard's transaction body
    /// (<c>NoteEndpoints.DiscardTheRow</c>, run through
    /// <see cref="AtomicWrites.WriteAtomicallyAsync"/>): a SELECT for the row, the DELETE,
    /// and the INSERT of its audit entry — three commands, each independently allowed
    /// <see cref="Command"/>, all inside ONE unit that the execution strategy retries as a
    /// whole. On the contended path the DELETE's concurrency failure adds a re-read, so
    /// three is the ordinary case rather than the absolute worst; the absolute worst is
    /// bounded by <see cref="Ceiling"/> regardless, which is what stopped this number
    /// having to be exactly right.
    ///
    /// A RETRY BUDGET THAT MODELS ONE COMMAND PER ATTEMPT IS SHORT BY THIS FACTOR, and it
    /// errs in the unsafe direction: it makes the derived request bound smaller than the
    /// retries it is supposed to contain, so the middleware cancels the wake-up on attempt
    /// three with three retries unspent. That was the state of this file for two rounds.
    ///
    /// MEASURED, NOT COUNTED BY READING.
    /// <c>RequestBoundsTests.The_discard_issues_the_commands_the_budget_models</c> counts
    /// the commands executed between the transaction starting and it committing, on a real
    /// discard against a real database, and fails if the body ever grows another.
    /// </summary>
    public const int DiscardCommandsPerAttempt = 3;

    /// <summary>
    /// The longest a single operation can stay alive inside the retry policy.
    ///
    /// <paramref name="maxRetryCount"/> retries means <c>maxRetryCount + 1</c> attempts,
    /// each issuing <paramref name="commandsPerAttempt"/> commands allowed
    /// <paramref name="commandTimeout"/> apiece, with a backoff of up to
    /// <paramref name="maxRetryDelay"/> between consecutive attempts.
    ///
    /// THE BACKOFF TERM IS DELIBERATELY CONSERVATIVE. EF's real delay is
    /// <c>min(1s x (2^i - 1) x [1, 1.1), MaxRetryDelay)</c> — 0, 1.1, 3.3, 7.7, 10, so
    /// about 22 seconds against the 50 modelled here. Erring long on a term that only
    /// widens the bound is safe; erring short on the command term, which is what
    /// <paramref name="commandsPerAttempt"/> exists to stop, is not.
    ///
    /// A function, not just a constant, so the relationship can be exercised at a scale a
    /// test can afford to wait for.
    /// </summary>
    public static TimeSpan RetryBudgetFor(
        TimeSpan commandTimeout, int maxRetryCount, TimeSpan maxRetryDelay,
        int commandsPerAttempt) =>
        (commandTimeout * commandsPerAttempt * (maxRetryCount + 1))
        + (maxRetryDelay * maxRetryCount);

    /// <summary>
    /// The request bound that contains <see cref="RetryBudgetFor"/> instead of cutting it
    /// short: the budget, plus one command of grace.
    ///
    /// WHAT THIS DOES AND DOES NOT GUARANTEE, stated because the previous version of this
    /// comment promised the second one. It guarantees that NO SINGLE RETRIED OPERATION is
    /// truncated by the middleware — which is the whole reason the derivation exists, since
    /// the operation most likely to need its retries is the first query of the day against
    /// an auto-paused Azure SQL. It does NOT guarantee that a request performing SEVERAL
    /// such operations in sequence completes: the provider lookup in middleware, the
    /// endpoint's own read and the transaction body each carry a budget of their own, and a
    /// request that needs all three in full has already lost. Cutting that one off is the
    /// bound doing its job.
    /// </summary>
    public static TimeSpan RequestTimeoutFor(
        TimeSpan commandTimeout, int maxRetryCount, TimeSpan maxRetryDelay,
        int commandsPerAttempt) =>
        RetryBudgetFor(commandTimeout, maxRetryCount, maxRetryDelay, commandsPerAttempt)
        + commandTimeout;

    /// <summary>
    /// What the configured policy can spend on one operation: 9 minutes 50 seconds.
    ///
    /// Six attempts of three commands of thirty seconds, plus five backoffs of up to ten.
    /// Quoted in <see cref="Practice.Infrastructure.Identity.IAuditWriter"/>, which used to
    /// be bounded by this and by nothing else.
    /// </summary>
    public static readonly TimeSpan RetryBudget =
        RetryBudgetFor(Command, MaxRetryCount, MaxRetryDelay, DiscardCommandsPerAttempt);

    /// <summary>
    /// The bound on a whole request, for the work that can be abandoned: 10 minutes
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
    /// IT IS NOT THE CEILING ON A REQUEST. RequestTimeoutsMiddleware cancels
    /// <c>RequestAborted</c> and then awaits the pipeline, so this bounds what observes a
    /// token and nothing else. Audit writes deliberately observe none.
    /// <see cref="Ceiling"/> is the ceiling.
    /// </summary>
    public static readonly TimeSpan Request =
        RequestTimeoutFor(Command, MaxRetryCount, MaxRetryDelay, DiscardCommandsPerAttempt);

    /// <summary>
    /// What the uncancellable work gets once <see cref="Request"/> has fired: 90 seconds,
    /// ONCE per request rather than once per write.
    ///
    /// Three commands' worth, which is what is reachable after cancellation on the longest
    /// path — an audit write in flight inside the transaction body, the commit, and the
    /// refusal row the discard's <c>finally</c> writes. That enumeration sizes the
    /// ALLOWANCE and nothing else: <see cref="UncancellableWriteDeadline"/> shares one
    /// deadline across every write in the request, so an enumeration that turns out to be
    /// short costs an abandoned audit row rather than a broken <see cref="Ceiling"/>. This
    /// project has been bitten four times by an enumeration that was complete when it was
    /// written; the failure mode here is deliberately the survivable one.
    /// </summary>
    public static readonly TimeSpan UncancellableGrace = Command * 3;

    /// <summary>
    /// THE CEILING ON A REQUEST — the number the BFF has to sit above: 11 minutes
    /// 50 seconds.
    ///
    /// <see cref="Request"/> bounds everything that observes a cancellation token.
    /// <see cref="UncancellableGrace"/> bounds everything that deliberately does not, from
    /// the moment the first bound fires. There is nothing else: the sum is the whole of
    /// what this tier will spend before answering.
    ///
    /// MEASURED ON THE REAL PATH, not asserted from these constants.
    /// <c>RequestBoundsTests.The_ceiling_is_the_request_bound_plus_the_uncancellable_tail</c>
    /// stalls the audit table, sends the DELETE, and times the response — proving both
    /// halves at once, since a response that arrived before <see cref="Request"/> would
    /// mean there was no tail to bound, and one that arrived after this would mean the
    /// bound does not hold.
    /// </summary>
    public static readonly TimeSpan Ceiling = Request + UncancellableGrace;
}
