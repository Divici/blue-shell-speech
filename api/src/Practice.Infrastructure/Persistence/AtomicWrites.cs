using Microsoft.EntityFrameworkCore;

namespace Practice.Infrastructure.Persistence;

/// <summary>
/// The house pattern for writes that must commit together or not at all.
///
/// EVERY explicit transaction in this API goes through here. Not because wrapping
/// BeginTransactionAsync saves typing — it does not — but because the four things that
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
///   3. The retry loop gets the caller's token, not CancellationToken.None.
///      The token-less ExecuteAsync overload waits out the full backoff on a request
///      nobody is attached to any more — five retries up to ten seconds apart, holding a
///      pooled connection on a container that scales to zero, before waking up to begin a
///      transaction on a token that has been cancelled the whole time.
///
///   4. The commit runs on CancellationToken.None — NOT on the uncancellable-write
///      deadline, and that distinction was worth an argument.
///      Everything before it is abandonable — if the request goes away the transaction
///      disposes uncommitted and nothing happened. Once the writes are staged the decision
///      is made, and abandoning the commit is the one remaining action that could leave the
///      set half-applied.
///      Every other uncancellable write in this application runs on
///      UncancellableWriteDeadline.Token, so that DatabaseTimeouts.Ceiling is a number
///      somebody can state. This one deliberately does not, because the deadline would
///      make it WORSE rather than bounded: DbTransaction.CommitAsync checks the token
///      before it starts and returns a cancelled task, so an expired deadline would roll
///      back a decision that has already been taken — losing the delete AND the audit row
///      inside it, which is precisely what this helper exists to prevent. SqlClient runs
///      the round trip synchronously anyway, so a token could never interrupt one in
///      flight; it could only refuse to begin. BEGIN and COMMIT are therefore outside the
///      ceiling, and DatabaseTimeouts.Ceiling says so rather than claiming to cover them.
///
/// THE CONTRACT ON THE BODY: it runs more than once, so it must RE-READ every entity it
/// touches, RE-CHECK every rule it depends on, and CONSTRUCT every entity it inserts, on
/// each attempt. Nothing tracked before the call survives into it — the Clear sees to that
/// — and an entity captured from an earlier attempt is either double-inserted or, once a
/// save has given it a store-generated key, rejected outright. Values may cross the
/// boundary. Tracked objects may not, and neither may conclusions drawn about them.
///
/// THAT LAST CLAUSE IS THE EXPENSIVE ONE, and it is written down because it has already
/// been broken. DiscardDraft read a note, satisfied itself that the note was an empty
/// unsigned draft, and then deleted whatever the body's re-read returned — which, if an
/// autosave committed in between, was a different row with a child's clinical record in
/// it. Before this helper existed the DELETE carried the checked row's RowVersion and the
/// mismatch raised DbUpdateConcurrencyException; afterwards it carried the current one and
/// matched. A guard turned into an accomplice, silently, in a commit whose purpose was to
/// make retries safe.
///
/// WHAT THE HELPER ENFORCES, AND WHAT IT ONLY ASKS FOR. The two guards below turn the
/// half of the contract that is mechanically checkable into a loud failure: no changes may
/// be staged before the call, and none may be left unsaved by the body. Re-reading and
/// re-checking cannot be enforced from here — nothing in the type system distinguishes a
/// fact about a row from a fact the body just made up — so those stay as prose, backed by
/// tests that force a write to land between a caller's two reads
/// (InterleavesOneWriteBeforeTheSecondRead).
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
    /// <exception cref="InvalidOperationException">
    /// The caller staged changes before the call, or the body left changes unsaved. Both
    /// used to be silent, and both silently lose a write.
    /// </exception>
    public static async Task WriteAtomicallyAsync(
        this PracticeDbContext db,
        Func<CancellationToken, Task> writes,
        CancellationToken ct)
    {
        /*
         * BEFORE the first Clear, because the Clear is what would destroy the evidence.
         *
         * The next adopter of this helper was going to be AmendNote, where the previous
         * version's IsCurrent = false flip is staged by note.Amend(reason) on a tracked
         * entity, before any of this runs. Wrapped as written, the Clear discards that
         * flip and the amendment inserts as a second current row — refused by
         * UX_ClinicalNotes_OneCurrentPerAppointment. Move the Add inside as the contract
         * asks and forget the flip, and the index is satisfied: the endpoint answers 201
         * Created, with a Location header, for a version that does not exist.
         *
         * Neither failure names this helper, and neither is visible in a passing run. A
         * contract that cannot be honoured by accident should not be able to be broken by
         * accident either.
         */
        if (db.ChangeTracker.HasChanges())
        {
            throw new InvalidOperationException(
                "WriteAtomicallyAsync clears the change tracker on every attempt, so changes "
                + "staged before the call are discarded rather than saved. Stage them inside "
                + $"the body. Staged: {Staged(db)}.");
        }

        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async attempt =>
        {
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(attempt);

            await writes(attempt);

            /*
             * CommitAsync commits a transaction, not a change tracker.
             *
             * A body that adds an entity and forgets to save it commits an empty
             * transaction, and the caller answers as though the write happened — the same
             * silence as the guard above, one layer in, reached by the same mistake. The
             * throw rolls the transaction back on the way out, so nothing is half-applied.
             */
            if (db.ChangeTracker.HasChanges())
            {
                throw new InvalidOperationException(
                    "WriteAtomicallyAsync commits the transaction, not the change tracker. The "
                    + "body left changes unsaved, and committing would discard them silently. "
                    + $"Still staged: {Staged(db)}.");
            }

            await transaction.CommitAsync(CancellationToken.None);
        }, ct);
    }

    /// <summary>
    /// What is staged, as TYPES AND STATES ONLY.
    ///
    /// This string ends up in an exception message, which reaches a log and — through
    /// problem details — the edge of the `web` → `api` boundary. "ClinicalNote Modified"
    /// tells the developer everything the diagnosis needs; a property value would put a
    /// child's clinical record in a log line (docs/SECURITY.md).
    /// </summary>
    private static string Staged(PracticeDbContext db) =>
        string.Join(", ", db.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added
                or EntityState.Modified
                or EntityState.Deleted)
            .Select(entry => $"{entry.Metadata.ClrType.Name} {entry.State}")
            .Distinct()
            .Order());
}
