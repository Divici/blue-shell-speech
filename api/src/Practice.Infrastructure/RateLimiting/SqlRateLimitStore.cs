using Microsoft.EntityFrameworkCore;
using Practice.Infrastructure.Persistence;

namespace Practice.Infrastructure.RateLimiting;

/// <summary>
/// What one attempt against one partition cost, and whether it was allowed.
/// </summary>
/// <param name="Allowed">Whether the request may proceed.</param>
/// <param name="CrossedTheLimit">
/// Whether THIS request is the one that took the partition past its limit.
///
/// It exists so the audit row is written once per partition per window rather than once per
/// refused request. See <see cref="IRateLimitStore"/> for why that is the right shape and
/// what it costs.
/// </param>
/// <param name="RetryAfter">
/// How long until the window ends. Computed whether or not the policy announces it — a
/// policy that keeps it to itself still has to know it (nothing does, today, but a value
/// computed conditionally is a value that is wrong the first time somebody reads it).
/// </param>
public readonly record struct RateLimitDecision(
    bool Allowed, bool CrossedTheLimit, TimeSpan RetryAfter);

/// <summary>
/// Counts one request against one partition, and says whether it may proceed.
///
/// NO <c>CancellationToken</c> ON THIS SEAM, for exactly the reason
/// <see cref="Practice.Infrastructure.Identity.ILoginBookkeeping"/> has none and
/// <see cref="Practice.Infrastructure.Identity.IAuditWriter"/> has none. A request that has
/// arrived has arrived, and the caller going away does not un-arrive it. Hand this the
/// request's token and an attacker skips the limiter entirely by closing the socket after
/// each guess — which is not a hypothetical shape, it is D075's finding on the audit path
/// repeated on the one control that is supposed to stop a flood. The surest way to stop a
/// call site passing a token it has in scope is to leave it nothing to pass, and with no
/// parameter CA2016 has nothing to enforce.
///
/// So it runs on <see cref="UncancellableWriteDeadline"/> like every other uncancellable
/// write in this tier, inside <c>DatabaseTimeouts.Ceiling</c>, and competes for the same
/// grace as the audit row that may follow it. The ORDER is therefore a control and it is
/// forced rather than chosen here: the refusal is not a fact until this returns, so the row
/// asserting one cannot be written before it (D097's rule — a row goes at the earliest point
/// at which the fact it asserts is already true, and not before).
///
/// AUDITED ONCE PER WINDOW, NOT ONCE PER REFUSAL, which is what
/// <see cref="RateLimitDecision.CrossedTheLimit"/> is for. A row per refused request would
/// make the audit table the amplification target the limiter exists to close: an attacker
/// who is being refused would still be writing 4,000-character rows into a serverless
/// database at whatever rate they can dial, in the one table this application never deletes
/// from. One row per partition per window is bounded by the number of partitions and windows
/// rather than by the attacker's request rate, and it answers the question an investigator
/// actually asks — "when did this start, and how long did it go on" — because the next
/// window writes another one.
///
/// WHAT THAT COSTS: if the audit write for the crossing request fails, the rest of that
/// window is refused silently. The next window writes a row, so a sustained attack is still
/// visible within one window's length; a burst that fits inside a single window and loses
/// its one row leaves nothing. Stated rather than hidden — the alternative was a row per
/// request, and that trade is worse in the case that matters.
/// </summary>
public interface IRateLimitStore
{
    /// <summary>Counts one request against <paramref name="partitionKey"/>.</summary>
    Task<RateLimitDecision> ConsumeAsync(RateLimitPolicy policy, string partitionKey);
}

/// <inheritdoc cref="IRateLimitStore"/>
public sealed class SqlRateLimitStore(
    PracticeDbContext db,
    UncancellableWriteDeadline deadline,
    TimeProvider clock) : IRateLimitStore
{
    /// <summary>
    /// Expired rows removed per request, at most.
    ///
    /// THE TABLE HAS TO DRAIN AT LEAST AS FAST AS IT FILLS, and the thing filling it is
    /// attacker-controlled: a caller who rotates the address they submit mints a fresh
    /// account partition every request, so a store that only ever resets windows in place
    /// grows one row per distinct key ever seen, forever, on a database billed for the space
    /// and for being awake. Sixteen is comfortably more than the one row a request can add,
    /// so the sweep outruns the flood by a factor of sixteen while costing one seek on the
    /// <c>WindowEndsAtUtc</c> index.
    ///
    /// It runs AFTER the commit and under <c>READPAST</c>, so it never waits on a row
    /// another request is holding and never becomes a deadlock partner for the statement
    /// above it. Rows it skips are picked up by the next request.
    /// </summary>
    private const int SweepBatch = 16;

    /// <summary>
    /// How stale a window has to be before the sweep will take it: one full window past its
    /// end, so a row that is about to be reused is never deleted out from under a caller
    /// mid-window. Deleting one would not be a correctness failure — the next request would
    /// re-create it — but it would silently give that partition a fresh allowance, which is
    /// the one direction this file must not err in.
    /// </summary>
    private static TimeSpan SweepDelayFor(RateLimitPolicy policy) => policy.Window;

    public async Task<RateLimitDecision> ConsumeAsync(RateLimitPolicy policy, string partitionKey)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var windowEnds = now.Add(policy.Window);
        var sweepBefore = now.Subtract(SweepDelayFor(policy));
        var limit = policy.Limit;

        /*
         * ONE STATEMENT THE DATABASE SERIALISES, AND THAT IS THE ENTIRE CONTROL — the same
         * argument LoginBookkeeping makes about AccessFailedCount, applied to the counter
         * that stands in front of it (D097, D098).
         *
         * A read-modify-write through EF here would lose increments under exactly the
         * traffic a rate limiter exists to see: twenty concurrent requests read one count,
         * twenty compute the same successor, one write wins. That was MEASURED on the
         * lockout — eighty attempts, four counted — and a limiter with the same defect is a
         * limiter that raises its own ceiling by however wide the caller can be. So the
         * increment, the window roll and the cap are all decided by the engine against the
         * row it is holding an exclusive lock on.
         *
         * RAW, PARAMETERISED SQL rather than ExecuteUpdateAsync, for LoginBookkeeping's
         * reason: the atomicity IS the control, so the statement that provides it is written
         * down here and reviewable rather than being whatever a LINQ ternary translates to
         * this quarter. Every hole is a DbParameter — FromSqlInterpolated makes that
         * unavoidable, no concatenation reaches the engine (docs/THREAT_MODEL.md ③).
         *
         * WHY UPDATE-THEN-INSERT UNDER (UPDLOCK, HOLDLOCK) AND NOT MERGE. The UPDATE takes a
         * range lock on the key even when it matches nothing, so a second caller arriving
         * with the same brand-new key blocks until this transaction commits and then finds
         * the row rather than inserting a duplicate. MERGE would express the same thing in
         * one statement and has a long history of doing it wrong under concurrency; this
         * pattern is the boring one.
         *
         * THE `CASE` ARITHMETIC IS ALL-AT-ONCE, like the lockout's. Every right-hand side
         * below is evaluated against the row's PRE-UPDATE values, so the WindowEndsAtUtc the
         * count test reads is the same one the window assignment beside it tests.
         *
         *   window expired  ->  Count = 1        (a new window, this request is its first)
         *   already refused ->  Count unchanged  (capped one past the limit, so a flood
         *                                         cannot overflow the column or hide the
         *                                         crossing request behind a moving target)
         *   otherwise       ->  Count + 1
         *
         * `Before` is the count standing in the CURRENT window before this request, which is
         * zero when the window just rolled — that is what the CASE in the OUTPUT clause is
         * doing, and it is what lets the caller tell the request that CROSSED the limit from
         * the ones behind it without a second read.
         *
         * NOT IDEMPOTENT UNDER A TRANSIENT RETRY, stated because D071 F2 was exactly this
         * class going unstated. If the batch commits and the connection then fails before
         * the result set is read, EF's execution strategy re-runs it and the partition is
         * charged twice. That errs toward over-throttling — a legitimate caller loses one
         * request out of a window's allowance, on a database that has just failed a command
         * — and the alternative is a dedupe token per request, which is a second row per
         * request in the table this sweep exists to keep small. The direction is the safe
         * one and it is chosen rather than overlooked.
         */
        var outcome = await db.Set<RateLimitOutcome>()
            .FromSqlInterpolated(
                $"""
                 SET NOCOUNT ON;
                 SET XACT_ABORT ON;

                 DECLARE @outcome TABLE (
                     [Before] int NOT NULL,
                     [After] int NOT NULL,
                     [ResetsAtUtc] datetime2(3) NOT NULL);

                 BEGIN TRANSACTION;

                 UPDATE [RateLimitCounters] WITH (UPDLOCK, HOLDLOCK)
                 SET [Count] = CASE
                         WHEN [WindowEndsAtUtc] <= {now} THEN 1
                         WHEN [Count] > {limit} THEN [Count]
                         ELSE [Count] + 1 END,
                     [WindowEndsAtUtc] = CASE
                         WHEN [WindowEndsAtUtc] <= {now} THEN {windowEnds}
                         ELSE [WindowEndsAtUtc] END
                 OUTPUT
                     CASE WHEN deleted.[WindowEndsAtUtc] <= {now} THEN 0
                          ELSE deleted.[Count] END,
                     inserted.[Count],
                     inserted.[WindowEndsAtUtc]
                 INTO @outcome
                 WHERE [PartitionKey] = {partitionKey};

                 IF NOT EXISTS (SELECT 1 FROM @outcome)
                     INSERT INTO [RateLimitCounters]
                         ([PartitionKey], [Count], [WindowEndsAtUtc])
                     OUTPUT 0, inserted.[Count], inserted.[WindowEndsAtUtc] INTO @outcome
                     VALUES ({partitionKey}, 1, {windowEnds});

                 COMMIT;

                 DELETE TOP ({SweepBatch}) FROM [RateLimitCounters] WITH (READPAST)
                 WHERE [WindowEndsAtUtc] <= {sweepBefore};

                 SELECT [Before], [After], [ResetsAtUtc] FROM @outcome;
                 """)
            .ToListAsync(deadline.Token);

        if (outcome.Count != 1)
        {
            /*
             * FAIL LOUD RATHER THAN OPEN.
             *
             * A limiter that cannot read its own counter has not decided anything, and the
             * two ways of answering that are a 500 or a silently unlimited endpoint. This
             * one is unreachable by construction — the batch above produces exactly one row
             * on every path through it — which is why it throws rather than guessing: an
             * empty result here means the statement was edited into a shape nobody
             * understands, and continuing would mean the login endpoint quietly stopped
             * being limited.
             */
            throw new InvalidOperationException(
                $"The rate limit statement for policy '{policy.Name}' returned "
                + $"{outcome.Count} rows rather than one. The limiter cannot decide, and "
                + "answering as though it had allowed the request would turn a failed "
                + "control into an absent one.");
        }

        var row = outcome[0];
        var allowed = row.After <= limit;
        var retryAfter = row.ResetsAtUtc - now;

        return new RateLimitDecision(
            Allowed: allowed,
            CrossedTheLimit: !allowed && row.Before == limit,
            RetryAfter: retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromSeconds(1));
    }
}
