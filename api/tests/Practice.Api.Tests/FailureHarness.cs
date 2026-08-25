using System.Reflection;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Practice.Domain.Auditing;
using Practice.Infrastructure.Identity;
using Practice.Infrastructure.Persistence;

namespace Practice.Api.Tests;

/*
 * BREAKING THINGS ON PURPOSE.
 *
 * Several of this system's guarantees are only observable when something downstream fails.
 * "The row and its audit entry commit together" cannot be proven by a run where both
 * succeed — it looks identical to two saves one after the other. "One submission writes
 * one audit row" cannot be proven by a run that never retries.
 *
 * These are shared rather than copied into each test class. The first version lived
 * privately inside NoteImmutabilityTests, and duplicating it for the second caller would
 * have produced two harnesses drifting apart — which is the pattern ORCHESTRATION.md's
 * fix-round brief is about: the sibling nobody updated.
 */

/// <summary>A failure the execution strategy below treats as transient. Nothing else does.</summary>
internal sealed class TransientBlipException()
    : Exception("A transient failure, raised on purpose.");

/// <summary>
/// Retries on <see cref="TransientBlipException"/> and on nothing else.
///
/// Deliberately not SqlServerRetryingExecutionStrategy with an added error number: a real
/// transient SQL error cannot be raised on demand, and simulating one by picking an error
/// code that SQL Server also raises for other reasons would make the test depend on the
/// engine's mood. What is under test is the BODY's behaviour on a second attempt, so the
/// trigger for that attempt should be the least interesting part of the setup.
/// </summary>
internal sealed class BlipRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
    : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(10))
{
    protected override bool ShouldRetryOn(Exception exception) =>
        exception is TransientBlipException;
}

/// <summary>
/// Retries on <see cref="TransientBlipException"/> after a wait long enough to measure.
///
/// <see cref="BlipRetryingExecutionStrategy"/> deliberately waits almost no time, because
/// most tests want the second attempt and not the pause before it. This one exists for the
/// questions where the pause IS the subject — does a cancelled caller sit through the
/// backoff, and does the request timeout outlive a retry that is still in progress — so the
/// delay has to be large enough that its presence or absence is unambiguous.
///
/// FIXED rather than the inherited exponential-with-jitter. The assertions are about a wait
/// happening or not happening, and a delay that varies between 0.8 and 1.2 seconds run to
/// run gives them nothing to sit comfortably either side of.
/// </summary>
internal sealed class SlowlyRetryingExecutionStrategy : ExecutionStrategy
{
    /// <summary>The wait a cancelled token is supposed to cut short.</summary>
    public static readonly TimeSpan CancellationBackoff = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _fixedBackoff;

    public SlowlyRetryingExecutionStrategy(
        ExecutionStrategyDependencies dependencies, int retries, TimeSpan backoff)
        : base(dependencies, retries, backoff) => _fixedBackoff = backoff;

    protected override bool ShouldRetryOn(Exception exception) =>
        exception is TransientBlipException;

    // base still decides WHETHER there is a next attempt (the retry count, the deadline);
    // only the length of the wait is overridden.
    protected override TimeSpan? GetNextDelay(Exception lastException) =>
        base.GetNextDelay(lastException) is null ? null : _fixedBackoff;
}

/// <summary>
/// Tracks the audit row, then fails — once, before the save.
///
/// The order matters and is the whole point: the entity is Added and the write then fails
/// with that entity still tracked and still <c>Added</c>. That is the state a transient
/// failure against a real database leaves behind, because a <c>SaveChanges</c> that failed
/// never calls <c>AcceptAllChanges</c> — and it is the state the next attempt inserts a
/// second time if nothing clears the tracker. A writer that threw BEFORE tracking anything
/// would leave a clean change tracker and prove nothing.
///
/// It throws rather than letting <c>SaveChangesAsync</c> fail because a real transient
/// failure cannot be raised on demand; what matters is the tracker state on the way out,
/// which is identical either way. (The docstring here used to say "the save is what
/// breaks", which is not what the code does — the D070 defect class inside the harness
/// written to enforce D070.)
/// </summary>
internal sealed class BlipsOnceAuditWriter(PracticeDbContext db) : IAuditWriter
{
    private bool _blipped;

    public async Task WriteAsync(AuditEvent auditEvent)
    {
        db.AuditEvents.Add(auditEvent);

        if (!_blipped)
        {
            _blipped = true;
            throw new TransientBlipException();
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }
}

/// <summary>
/// An IAuditWriter that cannot write, to force the failure an atomicity claim is about.
/// Nothing in a passing run can distinguish "committed together" from "committed one after
/// the other" — only a broken second write can.
/// </summary>
internal sealed class UnwritableAuditWriter : IAuditWriter
{
    public Task WriteAsync(AuditEvent auditEvent) =>
        throw new InvalidOperationException("The audit table is unavailable.");
}

/// <summary>
/// Lands one write in the gap between a request's FIRST read of a table and its SECOND.
///
/// THAT GAP IS NOT HYPOTHETICAL AND IT IS NOT NARROW. A request that validates a row and
/// then re-reads it inside a transaction is looking at the database twice, and anything
/// may commit in between — an autosave from the same clinician's editor, most obviously.
/// Whatever the first read decided is a statement about a row that no longer exists in
/// that form.
///
/// FORCED RATHER THAN RACED. Two live requests reproduce this once in a few thousand runs
/// and never in CI, which is the same as not testing it. Counting reads of one table and
/// running the interleaved write immediately before the second makes the ordering a
/// certainty instead of a coincidence.
///
/// ARMED EXPLICITLY, and counting nothing until it is. A host reads on its own account
/// before any test does — ProviderSeeder asks whether a provider exists at startup, and
/// the identity stores read on their own schedule — so a counter that started at
/// construction would be counting somebody else's queries. Arm after the client is built
/// and immediately before the request under test, and the two reads counted are the two
/// reads meant. A test using this must also seed through a DIFFERENT factory, so its setup
/// is not part of the count either.
/// </summary>
internal sealed class InterleavesOneWriteBeforeTheSecondRead(
    string table, Func<Task> interleavedWrite) : DbCommandInterceptor
{
    private const int Disarmed = int.MinValue;

    private int _reads = Disarmed;

    /// <summary>Starts counting. Call it immediately before the request under test.</summary>
    public void Arm() => Interlocked.Exchange(ref _reads, 0);

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains($"FROM [{table}]", StringComparison.Ordinal)
            && Interlocked.Increment(ref _reads) == 2)
        {
            await interleavedWrite();
        }

        return result;
    }
}

/// <summary>
/// Lands one write in the gap between a request's last READ of a table and its DELETE
/// from it.
///
/// <see cref="InterleavesOneWriteBeforeTheSecondRead"/> cannot reach this window by
/// construction: it fires before the second SELECT, so whatever it does is visible to that
/// SELECT and to every check made from it. THIS one fires after all of that has been
/// decided and immediately before the statement that acts on the decision — which is the
/// only place a `RowVersion` predicate can go stale, and therefore the only place
/// DbUpdateConcurrencyException can come from.
///
/// The gap is a real one and it is not small. Between the SELECT and the DELETE sits an
/// application round trip: a re-read, three branches, and EF building a command. Read
/// committed releases the shared lock the moment the SELECT is done, so an autosave from
/// the same clinician's editor is free to take the row and commit inside it.
///
/// ARMED EXPLICITLY, for the reason on the read interceptor above: the host and the
/// identity stores issue statements on their own account, and a counter running from
/// construction would be watching somebody else's.
///
/// BOTH EXECUTION PATHS, because which one EF uses is its business. A DELETE against a
/// table declared with HasTrigger cannot use an OUTPUT clause, so EF appends
/// <c>SELECT @@ROWCOUNT</c> and executes a reader; without the trigger it would be a
/// non-query. Intercepting one of the two would make this harness depend on a detail of
/// the schema it is testing against.
/// </summary>
internal sealed class InterleavesOneWriteBeforeTheDelete(
    string table, Func<Task> interleavedWrite) : DbCommandInterceptor
{
    private int _armed;
    private int _fired;

    /// <summary>Starts watching. Call it immediately before the request under test.</summary>
    public void Arm() => Interlocked.Exchange(ref _armed, 1);

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await InterleaveIfThisIsTheDeleteAsync(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await InterleaveIfThisIsTheDeleteAsync(command);
        return result;
    }

    /// <summary>
    /// Once, and only for the DELETE.
    ///
    /// The interleaved write goes through the ordinary endpoints, so it issues statements
    /// of its own against the same table — including, in one test, another DELETE. Firing
    /// once is what stops this recursing into itself.
    /// </summary>
    private async Task InterleaveIfThisIsTheDeleteAsync(DbCommand command)
    {
        if (_armed == 0
            || !command.CommandText.Contains($"DELETE FROM [{table}]", StringComparison.Ordinal)
            || Interlocked.Exchange(ref _fired, 1) == 1)
        {
            return;
        }

        await interleavedWrite();
    }
}

/// <summary>
/// Refuses to commit — the failure a row written inside a transaction does not survive.
///
/// "This audit row is durable" cannot be proven by a run where the transaction commits:
/// inside and outside look identical from the table afterwards. Only a commit that fails
/// tells them apart.
///
/// It throws its own exception type rather than a SqlException, deliberately.
/// SqlServerRetryingExecutionStrategy retries a list of known transient error numbers, so
/// a simulated one would either sit through the whole retry budget or depend on which
/// number was picked; what is under test is what survives a failed commit, not how many
/// times EF tries.
/// </summary>
internal sealed class FailsEveryCommit : DbTransactionInterceptor
{
    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default) =>
        throw new TransientBlipException();
}

/// <summary>
/// What one attempt of a transaction actually costs the database, counted rather than
/// assumed.
///
/// DatabaseTimeouts.RetryBudgetFor multiplies a command timeout by the number of commands
/// ONE ATTEMPT issues, and for two rounds that number was 1 while the discard's body
/// issued three. Nothing noticed, because a test that asserts an inequality between two
/// constants proves the constants and not the system. This is what the constant is checked
/// against: the commands EF really executes between a transaction starting and it ending.
///
/// A PAIR OF CLASSES SHARING STATE, because EF's interception surface is two base classes
/// — DbCommandInterceptor and DbTransactionInterceptor — and a type can only inherit one.
/// Implementing the interfaces directly would mean writing every member of both.
///
/// ARMED EXPLICITLY, for the reason the other interceptors here are: the host, the
/// identity stores and the seeding requests all issue statements on their own account, and
/// a counter running from construction would be watching somebody else's.
/// </summary>
internal sealed class CommandsPerTransaction
{
    private readonly List<int> _closed = [];
    private int _open = -1;

    /// <summary>Starts watching. Call it immediately before the request under test.</summary>
    public bool Armed { get; private set; }

    public void Arm() => Armed = true;

    /// <summary>
    /// One entry per transaction that opened and closed while armed, in order. A test
    /// asserting Single() on this is also asserting that the request opened exactly one
    /// transaction, which is worth having: a second one would mean the discard had stopped
    /// being atomic without anything else going red.
    /// </summary>
    public IReadOnlyList<int> Counts => _closed;

    public void TransactionOpened()
    {
        if (Armed) _open = 0;
    }

    public void CommandExecuted()
    {
        if (Armed && _open >= 0) _open++;
    }

    public void TransactionClosed()
    {
        if (!Armed || _open < 0) return;

        _closed.Add(_open);
        _open = -1;
    }
}

/// <inheritdoc cref="CommandsPerTransaction"/>
internal sealed class CountsCommandsInATransaction(CommandsPerTransaction tally)
    : DbCommandInterceptor
{
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        tally.CommandExecuted();
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        tally.CommandExecuted();
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        tally.CommandExecuted();
        return ValueTask.FromResult(result);
    }
}

/// <inheritdoc cref="CommandsPerTransaction"/>
internal sealed class MarksTransactionBoundaries(CommandsPerTransaction tally)
    : DbTransactionInterceptor
{
    public override ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection,
        TransactionEndEventData eventData,
        DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        tally.TransactionOpened();
        return ValueTask.FromResult(result);
    }

    public override Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        tally.TransactionClosed();
        return Task.CompletedTask;
    }

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        tally.TransactionClosed();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Makes every statement whose text contains a FRAGMENT hang, on whatever token EF handed
/// the command.
///
/// The token is the point. DelaysEveryRead below stalls everything and therefore only ever
/// demonstrates the cancellable half; this one is aimed at statements that deliberately run
/// on a token of their own, so the stall is only cut short by the bound that is actually
/// under test.
///
/// A FRAGMENT RATHER THAN A TABLE NAME, because the two halves of an authentication attempt
/// hit the SAME table and have to be stalled by different amounts. `FROM [AspNetUsers]` is
/// the lookup that decides the outcome; `UPDATE [AspNetUsers]` is the failure-count write
/// that runs afterwards. Stalling the table would stall both equally and could not
/// reproduce a login whose bookkeeping outlives the grace while its lookup does not — which
/// is the whole of WORK_QUEUE 1.17 F1. <see cref="StallsEveryStatementAgainst"/> is the
/// table-shaped special case, kept because two tests name it.
///
/// The stall stands in for a command that consumes its whole timeout. A real one cannot be
/// produced on demand — a wedged database is not something a test can arrange — and what
/// is under test is what bounds the wait, not what caused it.
/// </summary>
internal class StallsEveryStatementMatching(string fragment, TimeSpan stall)
    : DbCommandInterceptor
{
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await StallIfItTouchesTheTableAsync(command, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await StallIfItTouchesTheTableAsync(command, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await StallIfItTouchesTheTableAsync(command, cancellationToken);
        return result;
    }

    private Task StallIfItTouchesTheTableAsync(DbCommand command, CancellationToken ct) =>
        command.CommandText.Contains(fragment, StringComparison.Ordinal)
            ? Task.Delay(stall, ct)
            : Task.CompletedTask;
}

/// <summary>
/// Every statement touching ONE table, whatever it does to it.
///
/// Pointed at AuditEvents it reproduces the exact shape of the 1.16 F2 finding: the reads
/// before it complete normally, the request bound fires, and the uncancellable INSERT is
/// still going.
/// </summary>
internal sealed class StallsEveryStatementAgainst(string table, TimeSpan stall)
    : StallsEveryStatementMatching($"[{table}]", stall);

/// <summary>
/// Makes every read take longer than the caller is willing to wait.
///
/// A request timeout is only observable on a request that is slow, and nothing this
/// application does is slow on a laptop. Delaying at the EF layer puts the wait where the
/// production wait would be — a database resuming from auto-pause — and on the token the
/// endpoint is actually holding, so a timeout that fires cancels it exactly as it would in
/// front of Azure SQL.
/// </summary>
internal sealed class DelaysEveryRead(TimeSpan delay) : DbCommandInterceptor
{
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(delay, cancellationToken);
        return result;
    }
}

/// <summary>
/// A bookkeeping seam that reports every write as having changed nothing.
///
/// STANDS IN FOR TWO REAL THINGS, neither of which a test can arrange on demand: the
/// <c>DbUpdateConcurrencyException</c> that <c>UserStore.UpdateAsync</c> catches and returns
/// as a failed <c>IdentityResult</c> — the mechanism behind eighty concurrent guesses
/// counting as four — and a row that is gone by the time the statement reaches it.
///
/// The point is not the exotic cause. It is that the caller has to READ the answer: a login
/// that refuses a credential without counting it toward the lockout has quietly stopped
/// having a lockout, and the previous version of this path could not tell the difference.
/// </summary>
internal sealed class CountsNothing : ILoginBookkeeping
{
    public Task<bool> CountFailureAsync(string userId) => Task.FromResult(false);

    public Task<bool> ClearFailuresAsync(string userId) => Task.FromResult(false);

    public Task<bool> RecordMfaAsync(string userId, DateTime atUtc) => Task.FromResult(false);
}

/// <summary>
/// A path inside the repository, found by walking up from the test assembly.
///
/// The build output sits several directories below the tree and the depth differs between a
/// local run and CI, so walking up to the thing being asserted on is the version that
/// encodes neither. Files AND directories, because two of the guards that use this walk a
/// whole source tree rather than naming the files in it — which is the point of those
/// guards (D090's "glob, do not enumerate").
///
/// Shared rather than copied, for the reason at the top of this file. Two test classes now
/// assert on the source tree: one on the compliance documents, one on a call shape the
/// compiler cannot see.
/// </summary>
internal static class RepoTree
{
    public static string File(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
             dir is not null;
             dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (System.IO.File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            $"Could not find {relativePath} above {AppContext.BaseDirectory}.");
    }
}

/// <summary>
/// Reads one instance property down the WHOLE inheritance chain, public or not.
///
/// Two classes here need it and for the same reason: the value that decides a bound is
/// declared protected by a framework type, so the only honest way to assert the configured
/// value rather than restate a literal is to read it off the live object.
/// <c>ExecutionStrategy.MaxRetryCount</c> is one; <c>UserManager&lt;T&gt;.CancellationToken</c>
/// — the single lever binding every Identity store call — is the other.
///
/// <c>Type.GetProperty</c> searches the type it is asked about and its PUBLIC inheritance,
/// which is not enough: these members are declared on a base class and the object is a
/// derived one. Walking the chain with <c>DeclaredOnly</c> finds it either way, and throws
/// by name if the framework removes it rather than passing quietly.
///
/// Shared rather than copied, for the reason at the top of this file: the second copy is
/// the one nobody updates.
/// </summary>
internal static class ProtectedMember
{
    public static object Read(object instance, string name)
    {
        const BindingFlags Declared = BindingFlags.Instance
            | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;

        for (var type = instance.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperty(name, Declared);
            if (property is not null) return property.GetValue(instance)!;
        }

        throw new InvalidOperationException(
            $"{instance.GetType().Name} no longer exposes {name}. This value has to be read "
            + "from the configured object, not restated in a test.");
    }
}

internal static class FailureHarness
{
    /// <summary>Swaps in the retrying strategy and the writer that provokes it.</summary>
    public static void RetryOnceOnATransientBlip(
        string connectionString, IServiceCollection services)
    {
        // AddDbContext uses TryAdd, so the application's own options win unless the
        // existing registration is removed first.
        services.RemoveAll<DbContextOptions<PracticeDbContext>>();
        services.RemoveAll<DbContextOptions>();

        services.AddDbContext<PracticeDbContext>(options =>
            options.UseSqlServer(connectionString,
                sql => sql.ExecutionStrategy(deps => new BlipRetryingExecutionStrategy(deps))));

        services.AddScoped<IAuditWriter, BlipsOnceAuditWriter>();
    }

    /// <summary>
    /// The same one-blip retry, but with a backoff long enough to be a bound in its own
    /// right — the shape of a database resuming from auto-pause.
    ///
    /// <see cref="RetryOnceOnATransientBlip"/> retries in ten milliseconds, so a request
    /// running under it is never in the retry loop long enough for a request timeout to
    /// have an opinion. This one is for the test that asks whether the request bound
    /// contains the retry budget, which is a question about the wait and nothing else.
    ///
    /// THE COMMAND TIMEOUT IS A PARAMETER, and that is a fix rather than tidying. It used
    /// to be pinned at production's thirty seconds while the caller scaled every other
    /// term down to milliseconds and derived a request bound from ITS OWN number. The
    /// derived bound was therefore built on a command timeout the running application did
    /// not have, so the command term — the term that was wrong — could not affect the
    /// outcome at any value. A test cannot exercise an error it has configured away.
    /// </summary>
    public static void RetriesAfterAMeasurableWait(
        string connectionString, IServiceCollection services,
        int retries, TimeSpan backoff, TimeSpan commandTimeout,
        params IInterceptor[] interceptors)
    {
        services.RemoveAll<DbContextOptions<PracticeDbContext>>();
        services.RemoveAll<DbContextOptions>();

        services.AddDbContext<PracticeDbContext>(options => options
            .UseSqlServer(connectionString, sql =>
            {
                sql.ExecutionStrategy(
                    deps => new SlowlyRetryingExecutionStrategy(deps, retries, backoff));
                sql.CommandTimeout((int)commandTimeout.TotalSeconds);
            })
            .AddInterceptors(interceptors));

        services.AddScoped<IAuditWriter, BlipsOnceAuditWriter>();
    }

    /// <summary>
    /// The uncancellable-write deadline, scaled down so a test can wait for it.
    ///
    /// Production's is eleven minutes and fifty seconds; nothing can be measured against
    /// that.
    ///
    /// THE TWO ARGUMENTS ARE SEPARATE ON PURPOSE. In production the construction ceiling
    /// IS the request bound plus the grace, so deleting the middleware's BindTo leaves the
    /// same number by a different route and nothing goes red. A test that wants the
    /// binding to be the control has to make the fallback distinguishable, so it passes a
    /// deliberately distant backstop and asserts against the bound plus the grace.
    /// </summary>
    public static void BoundedBy(
        IServiceCollection services, TimeSpan backstop, TimeSpan grace) =>
        services.AddScoped(_ => new UncancellableWriteDeadline(backstop, grace));

    /// <summary>
    /// Registers an interceptor on the application's own DbContext.
    ///
    /// The options have to be replaced wholesale — AddDbContext uses TryAdd, so the
    /// application's registration wins otherwise — which means production's retry policy
    /// is restated here rather than inherited. Deliberate: an interceptor test that
    /// silently ran without retries would be testing a different pipeline from the one it
    /// names.
    /// </summary>
    public static Action<IServiceCollection> With(
        string connectionString, params IInterceptor[] interceptors) =>
        services =>
        {
            services.RemoveAll<DbContextOptions<PracticeDbContext>>();
            services.RemoveAll<DbContextOptions>();

            services.AddDbContext<PracticeDbContext>(options => options
                .UseSqlServer(connectionString, sql =>
                {
                    sql.EnableRetryOnFailure(
                        DatabaseTimeouts.MaxRetryCount, DatabaseTimeouts.MaxRetryDelay, null);
                    sql.CommandTimeout(DatabaseTimeouts.CommandSeconds);
                })
                .AddInterceptors(interceptors));
        };
}
