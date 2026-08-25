using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Practice.Infrastructure.Persistence;

namespace Practice.Infrastructure.Identity;

/// <summary>
/// The writes a login makes about itself: the failure count, its reset, and the moment the
/// second factor was last proved.
///
/// EVERY ONE OF THEM IS A SINGLE STATEMENT THE DATABASE SERIALISES, and that is the whole
/// reason this type exists rather than the <c>UserManager</c> methods it replaces.
///
/// WHAT WENT WRONG WITHOUT IT. <c>UserManager.AccessFailedAsync</c> is a read-modify-write:
/// it increments the count on an entity the request read earlier and hands it to
/// <c>UserStore.UpdateAsync</c>, which stamps a fresh <c>ConcurrencyStamp</c> and saves with
/// the OLD one in the WHERE clause. Twenty simultaneous wrong-password posts all read the
/// same stamp, so ONE UPDATE matches and the other nineteen raise
/// <c>DbUpdateConcurrencyException</c> — which that method catches and converts into an
/// <c>IdentityResult.Failed</c>, which the caller then discarded. Measured: four waves of
/// twenty, eighty attempts, <c>AccessFailedCount = 4</c>, <c>LockoutEnd = NULL</c>. The
/// five-failure lockout was buying an N-wide attacker N guesses per counted failure.
///
/// It also issued THREE round trips where one was needed. <c>UpdateUserAsync</c> runs the
/// user validators first, and <c>RequireUniqueEmail</c> makes that a lookup by normalised
/// name and another by normalised email — re-checking that an email is unique in order to
/// record that somebody typed the wrong password. Those two reads are what made a wrong
/// password measurably slower than an unknown address (docs/SECURITY.md §Authentication);
/// collapsing the write to one statement closed the timing oracle as a side effect of
/// closing the race.
///
/// WHY NOT A RETRY LOOP ON THE STAMP, which is the obvious repair. It converts a lost
/// increment into a queue: under twenty-way contention each attempt re-reads and re-writes
/// until it wins, so the work is quadratic in the width of the attack, every retry is
/// another round trip drawing on the ONE grace this request shares with its audit row
/// (D090), and the loop needs a bound — at which point a bounded loop can still lose a
/// count and the race is back. The database already has a serialising primitive for
/// exactly this and it costs one statement.
///
/// WHY NOT A LOCK. An in-process lock is not a lock: <c>web</c> and <c>api</c> are Container
/// Apps that scale horizontally, so it holds on one replica and lets the next one through.
/// <c>sp_getapplock</c> would hold across replicas and costs a round trip to take, a round
/// trip to release, and a lock held on a serverless database that is billed for being
/// awake — to serialise a single-row UPDATE the engine already serialises.
///
/// NO CancellationToken ON THIS SEAM, for the reason D075 gives about audit writes and
/// D092 gives about this one specifically. A failure count records something that already
/// happened, and the caller going away does not un-happen it — hand the request's token to
/// this and an attacker skips the lockout by closing the socket after each guess. The
/// surest way to stop a call site passing a token it has in scope is to leave it nothing to
/// pass, and with no parameter CA2016 has nothing to enforce. These run on
/// <see cref="UncancellableWriteDeadline"/>, like every other uncancellable write, so they
/// are inside <c>DatabaseTimeouts.Ceiling</c> and compete for the same grace — which is why
/// <see cref="ProviderAuthenticator"/> orders them against its audit writes deliberately.
///
/// RAW, PARAMETERISED SQL rather than <c>ExecuteUpdateAsync</c>. The atomicity is the
/// control, so the statement that provides it is written down here and reviewable rather
/// than being whatever a LINQ ternary translates to this quarter; SQL Server evaluates
/// every right-hand side of one UPDATE against the row's pre-update values, which is what
/// makes the CASE below consistent with the increment beside it. Interpolation goes through
/// <c>ExecuteSqlAsync</c>, which turns every hole into a <c>DbParameter</c> — no
/// concatenation reaches the engine (docs/THREAT_MODEL.md ③).
/// </summary>
public interface ILoginBookkeeping
{
    /// <summary>
    /// Counts one failed credential attempt, locking the account when the count reaches
    /// <c>IdentityOptions.Lockout.MaxFailedAccessAttempts</c>.
    ///
    /// Returns whether a row moved. FALSE for an id that matches nothing, which is not an
    /// error: <see cref="ProviderAuthenticator"/>'s unknown-email branch calls this against
    /// an id that cannot exist, so that a caller timing the endpoint sees the same round
    /// trip whether or not the address has an account.
    /// </summary>
    Task<bool> CountFailureAsync(string userId);

    /// <summary>Zeroes the failure count after a credential is accepted.</summary>
    Task<bool> ClearFailuresAsync(string userId);

    /// <summary>
    /// Records when the second factor was last proved — the timestamp re-authentication
    /// is measured from (docs/SECURITY.md §Authentication).
    /// </summary>
    Task<bool> RecordMfaAsync(string userId, DateTime atUtc);
}

/// <inheritdoc cref="ILoginBookkeeping"/>
public sealed class LoginBookkeeping(
    PracticeDbContext db,
    IOptions<IdentityOptions> identityOptions,
    UncancellableWriteDeadline deadline) : ILoginBookkeeping
{
    private IdentityOptions Options => identityOptions.Value;

    public async Task<bool> CountFailureAsync(string userId)
    {
        var threshold = Options.Lockout.MaxFailedAccessAttempts;
        var lockedUntil = DateTimeOffset.UtcNow.Add(Options.Lockout.DefaultLockoutTimeSpan);
        var stamp = Guid.NewGuid().ToString();

        /*
         * ONE statement, and the CASE is what makes the threshold part of it.
         *
         * Identity's own sequence is "increment, and if that reached the threshold then set
         * a lockout end and zero the count" — three decisions taken in application memory
         * against a value that was read before any of them. Here the engine takes all three
         * against the row it is holding an exclusive lock on, so twenty concurrent callers
         * produce twenty increments and the fifth of them is the one that locks the account.
         *
         * [AccessFailedCount] on the right-hand side is the value BEFORE this statement in
         * all three assignments — SQL Server's all-at-once semantics — so the count the
         * CASE tests and the count it writes are the same count.
         *
         * The stamp is rewritten for the same reason UserStore does it: anything still
         * holding the row as it was before this now fails its own concurrency check rather
         * than overwriting the count with a stale copy.
         */
        var rows = await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE [AspNetUsers]
             SET [AccessFailedCount] = CASE
                     WHEN [AccessFailedCount] + 1 >= {threshold} THEN 0
                     ELSE [AccessFailedCount] + 1 END,
                 [LockoutEnd] = CASE
                     WHEN [AccessFailedCount] + 1 >= {threshold} THEN {lockedUntil}
                     ELSE [LockoutEnd] END,
                 [ConcurrencyStamp] = {stamp}
             WHERE [Id] = {userId}
             """,
            deadline.Token);

        return rows > 0;
    }

    public async Task<bool> ClearFailuresAsync(string userId)
    {
        var stamp = Guid.NewGuid().ToString();

        // The lockout END is deliberately left alone, matching
        // UserStore.ResetAccessFailedCountAsync: a live lockout is not lifted by getting the
        // password right, or the lockout would only last until the attacker guessed once
        // more.
        var rows = await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE [AspNetUsers]
             SET [AccessFailedCount] = 0, [ConcurrencyStamp] = {stamp}
             WHERE [Id] = {userId}
             """,
            deadline.Token);

        return rows > 0;
    }

    public async Task<bool> RecordMfaAsync(string userId, DateTime atUtc)
    {
        var stamp = Guid.NewGuid().ToString();

        var rows = await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE [AspNetUsers]
             SET [LastMfaAtUtc] = {atUtc}, [ConcurrencyStamp] = {stamp}
             WHERE [Id] = {userId}
             """,
            deadline.Token);

        return rows > 0;
    }
}
