using Microsoft.EntityFrameworkCore;
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
/// Tracks the audit row, then fails the save — once.
///
/// The order matters and is the whole point: the entity is Added and the save is what
/// breaks, which is exactly the shape of a transient failure against a real database. A
/// writer that threw BEFORE tracking anything would leave a clean change tracker and prove
/// nothing.
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
}
