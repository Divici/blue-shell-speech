using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Practice.Application.Providers;
using Practice.Domain.Auditing;
using Practice.Infrastructure.Persistence;

namespace Practice.Api.Tests;

/// <summary>
/// The contract <see cref="AtomicWrites.WriteAtomicallyAsync"/> places on its callers,
/// tested where the contract lives rather than through whichever endpoint happens to use
/// it today.
///
/// Every explicit transaction in this API goes through that helper (D075), and its
/// contract was PROSE ONLY: the body runs more than once, nothing tracked before the call
/// survives into it, and values may cross the boundary while tracked objects may not. The
/// helper's own decision record admitted the gap — "the helper cannot enforce its own
/// contract" — and the next adopter was going to be AmendNote, where breaking it produces
/// 201 Created with a Location header for a row that was never inserted.
///
/// So the two halves that CAN be enforced now are, and these are the tests that hold them:
/// changes staged before the call, and changes the body left unsaved. Both used to be
/// silent. Both are now loud.
///
/// These build a DbContext directly rather than going through the API. The subject is the
/// helper, and routing it through an endpoint would put the endpoint's own correctness in
/// the way of a failure that belongs to the helper.
/// </summary>
[Collection(UsesSqlServer.Name)]
public sealed class AtomicWriteTests(SqlServerFixture sql)
{
    /// <summary>
    /// A context with no request behind it.
    ///
    /// <see cref="FixedProviderContext"/> with null is the honest value: there is no
    /// session here, and a null provider matches no patient rows rather than all of them.
    /// Nothing in this file touches a table with a tenancy filter.
    /// </summary>
    private static PracticeDbContext ContextWith(Action<DbContextOptionsBuilder<PracticeDbContext>> configure)
    {
        var options = new DbContextOptionsBuilder<PracticeDbContext>();
        configure(options);

        return new PracticeDbContext(options.Options, new FixedProviderContext(null));
    }

    private PracticeDbContext Context() =>
        ContextWith(options => options.UseSqlServer(sql.ConnectionString));

    /// <summary>
    /// An audit row is the least-constrained thing this schema can be asked to write: no
    /// foreign keys, no tenancy filter, no trigger. What is under test is the change
    /// tracker, so the entity should contribute nothing of its own.
    /// </summary>
    private static AuditEvent SomethingToWrite(Guid subject) =>
        AuditEvent.Record(
            AuditEventType.NoteDiscarded, AuditOutcome.Failure,
            entityType: nameof(AtomicWriteTests), entityPublicId: subject,
            metadata: "refused;reason=not-found");

    private async Task<int> RowsWrittenAsync(Guid subject)
    {
        await using var db = Context();
        return await db.AuditEvents.AsNoTracking().CountAsync(e => e.EntityPublicId == subject);
    }

    /// <summary>
    /// F2: a caller who stages a write before the call is told so, instead of losing it.
    ///
    /// THE SHAPE THIS CLOSES, in the words of the finding that found it. AmendNote is the
    /// next adopter: <c>note.Amend(reason)</c> stages v1's <c>IsCurrent = false</c> flip on
    /// the tracked entity, and the amendment is a second row. Wrapped as written, the
    /// <c>ChangeTracker.Clear()</c> at the top of the first attempt throws the flip away,
    /// the amendment inserts as a SECOND current row, and
    /// UX_ClinicalNotes_OneCurrentPerAppointment rejects it. Hoist the <c>Add</c> inside
    /// as the contract asks and forget the flip, and the index is satisfied — the endpoint
    /// then answers 201 Created with a Location for a version that does not exist, while
    /// the visit still shows v1 as current.
    ///
    /// Neither of those is a failure anyone would attribute to the helper. That is what
    /// makes a silent contract worse than a strict one.
    ///
    /// Control: the <c>HasChanges</c> guard on entry to AtomicWrites.WriteAtomicallyAsync.
    /// Deleted → red on the ThrowsAsync, "Assert.Throws() Failure: No exception was
    /// thrown, Expected: typeof(System.InvalidOperationException)" — the staged row is
    /// discarded by the Clear, the body's row commits, and nobody is told.
    /// </summary>
    [Fact]
    public async Task Changes_staged_before_the_call_are_refused_rather_than_discarded()
    {
        var staged = Guid.NewGuid();
        var inTheBody = Guid.NewGuid();

        await using var db = Context();

        // The caller's own work, exactly as AmendNote would stage it.
        db.AuditEvents.Add(SomethingToWrite(staged));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.WriteAtomicallyAsync(async attempt =>
            {
                db.AuditEvents.Add(SomethingToWrite(inTheBody));
                await db.SaveChangesAsync(attempt);
            }, CancellationToken.None));

        // Loud, and nothing half-written: the body never ran, so the row it would have
        // inserted is not there either.
        Assert.Equal(0, await RowsWrittenAsync(staged));
        Assert.Equal(0, await RowsWrittenAsync(inTheBody));
    }

    /// <summary>
    /// F2, the other half: a body that stages and does not save is refused too.
    ///
    /// The same silence one layer in. <c>CommitAsync</c> commits a transaction, not a
    /// change tracker — so a body that adds an entity and forgets to save commits an empty
    /// transaction, and the caller goes on to answer as though the write happened. That is
    /// the identical failure the entry guard closes, produced by the identical mistake, so
    /// it is the identical answer: throw and let the transaction roll back.
    ///
    /// Control: the <c>HasChanges</c> guard between the body and the commit in
    /// AtomicWrites.WriteAtomicallyAsync.
    /// Deleted → red on the ThrowsAsync, "Assert.Throws() Failure: No exception was
    /// thrown, Expected: typeof(System.InvalidOperationException)" — an empty transaction
    /// commits, and the caller reads that as the write having happened.
    /// </summary>
    [Fact]
    public async Task A_body_that_leaves_changes_unsaved_is_refused_rather_than_committed()
    {
        var neverSaved = Guid.NewGuid();

        await using var db = Context();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.WriteAtomicallyAsync(_ =>
            {
                db.AuditEvents.Add(SomethingToWrite(neverSaved));
                return Task.CompletedTask;
            }, CancellationToken.None));

        Assert.Equal(0, await RowsWrittenAsync(neverSaved));
    }

    /// <summary>
    /// A body that stages nothing at all is fine, and has to stay fine.
    ///
    /// DiscardDraft reaches exactly this state on two of its paths: the note is already
    /// gone, or the re-read found a row that may not be discarded after all and only an
    /// audit row was written — which the audit writer saves itself. A guard that read
    /// "nothing was written" as "somebody made a mistake" would turn both into 500s.
    /// </summary>
    [Fact]
    public async Task A_body_that_stages_nothing_commits_normally()
    {
        await using var db = Context();

        var ran = 0;
        await db.WriteAtomicallyAsync(_ => { ran++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(1, ran);
    }

    /// <summary>
    /// F4: a cancelled caller does not sit through the retry backoff.
    ///
    /// <c>strategy.ExecuteAsync(async () =&gt; …)</c> is the token-less overload. The
    /// helper's signature advertises a CancellationToken and the body received it, but the
    /// RETRY LOOP never did — so <c>Task.Delay</c> between attempts ran on
    /// CancellationToken.None. A clinician who taps Discard, gets a transient failure, and
    /// locks her phone leaves a request asleep for the full backoff and then waking up to
    /// begin a transaction on a token that has been cancelled the whole time. Against
    /// production's policy — five retries, up to ten seconds apart — that is most of a
    /// minute of a pooled connection held for nobody, on a container that scales to zero.
    ///
    /// A half-honoured parameter is the D072 class in a signature: it advertises a
    /// capability that does not exist, and the next caller believes it.
    ///
    /// The elapsed time is the assertion because it is the claim. Attempt counting looks
    /// tidier and proves nothing here — the second attempt begins its transaction on the
    /// cancelled token and throws before the body is re-entered, so a body-entry counter
    /// reads 1 whether the token reached the loop or not.
    ///
    /// Control: the <c>ct</c> argument on <c>strategy.ExecuteAsync</c> in
    /// AtomicWrites.WriteAtomicallyAsync.
    /// Deleted — reverted to the token-less overload — → red on the elapsed assertion,
    /// "The retry backoff is 00:00:05, and a cancelled caller waited 00:00:05.0195745 of
    /// it".
    /// </summary>
    [Fact]
    public async Task A_cancelled_write_does_not_sleep_out_the_retry_backoff()
    {
        await using var db = ContextWith(options => options.UseSqlServer(
            sql.ConnectionString,
            server => server.ExecutionStrategy(deps => new SlowlyRetryingExecutionStrategy(deps))));

        using var caller = new CancellationTokenSource();

        var elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            db.WriteAtomicallyAsync(_ =>
            {
                // The phone locks while the first attempt is in flight, and the attempt
                // fails transiently. Both are ordinary here: Azure SQL serverless
                // auto-pauses, and this app is used walking between houses.
                caller.Cancel();
                throw new TransientBlipException();
            }, caller.Token));

        elapsed.Stop();

        Assert.True(
            elapsed.Elapsed < SlowlyRetryingExecutionStrategy.Backoff / 2,
            $"The retry backoff is {SlowlyRetryingExecutionStrategy.Backoff}, and a cancelled "
            + $"caller waited {elapsed.Elapsed} of it.");
    }
}
