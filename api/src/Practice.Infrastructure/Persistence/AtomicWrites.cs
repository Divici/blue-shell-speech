using Microsoft.EntityFrameworkCore;

namespace Practice.Infrastructure.Persistence;

/// <summary>
/// The house pattern for writes that must commit together or not at all.
///
/// EVERY explicit transaction in this API goes through here. Not because wrapping
/// BeginTransactionAsync saves typing — it does not — but because the three things that
/// make an explicit transaction correct in THIS application are all easy to leave out, and
/// each of them is invisible until the day it matters:
///
///   1. The transaction lives INSIDE the execution strategy, never around it.
///      AddInfrastructure enables EnableRetryOnFailure, and a retrying strategy refuses a
///      user-initiated transaction it did not open: it has to own the retry boundary, or a
///      retry would resume inside a transaction that no longer exists.
///
///   2. The change tracker is cleared at the top of EVERY attempt, including the first.
///      A SaveChanges that FAILED does not call AcceptAllChanges, so everything it staged
///      is still tracked and still Added — and the next attempt inserts it a second time.
///      That is not theoretical here: Azure SQL serverless auto-pauses, so a retry is the
///      ordinary case rather than the exotic one, and the first thing this database ever
///      deleted wrote its audit row twice because of it.
///
///   3. The commit runs on CancellationToken.None.
///      Everything before it is abandonable — if the request goes away the transaction
///      disposes uncommitted and nothing happened. Once the writes are staged the decision
///      is made, and abandoning the commit is the one remaining action that could leave the
///      set half-applied.
///
/// THE CONTRACT ON THE BODY, which the helper cannot enforce and a caller must honour:
/// it runs more than once, so it must re-read every entity it touches and construct every
/// entity it inserts, on each attempt. Nothing tracked before the call survives into it —
/// the Clear sees to that — and an entity captured from an earlier attempt is either
/// double-inserted or, once a save has given it a store-generated key, rejected outright.
/// Values may cross the boundary. Tracked objects may not.
/// </summary>
public static class AtomicWrites
{
    /// <param name="db">The context whose writes are being grouped.</param>
    /// <param name="writes">
    /// Stages and saves the writes. Called once per attempt; see the contract above.
    /// The token it is handed is the caller's — everything before the commit is
    /// deliberately abandonable.
    /// </param>
    /// <param name="ct">The request's token.</param>
    public static async Task WriteAtomicallyAsync(
        this PracticeDbContext db,
        Func<CancellationToken, Task> writes,
        CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            await writes(ct);

            await transaction.CommitAsync(CancellationToken.None);
        });
    }
}
