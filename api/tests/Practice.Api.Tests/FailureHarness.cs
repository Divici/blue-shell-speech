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
/// every other test wants the second attempt and not the pause before it. This one exists
/// for the opposite question — does a cancelled caller still sit through the backoff — so
/// the delay is the thing under observation and has to be large enough that its absence is
/// unambiguous.
///
/// FIXED rather than the inherited exponential-with-jitter. The assertion is "the wait did
/// not happen", and a delay that varies between 0.8 and 1.2 seconds run to run gives that
/// assertion nothing to be comfortably below.
/// </summary>
internal sealed class SlowlyRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
    : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: Backoff)
{
    /// <summary>The wait a cancelled token is supposed to cut short.</summary>
    public static readonly TimeSpan Backoff = TimeSpan.FromSeconds(5);

    protected override bool ShouldRetryOn(Exception exception) =>
        exception is TransientBlipException;

    // base still decides WHETHER there is a next attempt (the retry count, the deadline);
    // only the length of the wait is overridden.
    protected override TimeSpan? GetNextDelay(Exception lastException) =>
        base.GetNextDelay(lastException) is null ? null : Backoff;
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
                    sql.EnableRetryOnFailure(maxRetryCount: 5, TimeSpan.FromSeconds(10), null);
                    sql.CommandTimeout(DatabaseTimeouts.CommandSeconds);
                })
                .AddInterceptors(interceptors));
        };
}
