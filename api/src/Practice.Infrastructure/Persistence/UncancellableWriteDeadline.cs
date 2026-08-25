using System.Diagnostics;

namespace Practice.Infrastructure.Persistence;

/// <summary>
/// The bound on work this application deliberately refuses to abandon when the caller goes
/// away — audit writes, today, and nothing else.
///
/// WHY THIS TYPE EXISTS. RequestTimeoutsMiddleware does not stop a request. It cancels
/// HttpContext.RequestAborted and then AWAITS the rest of the pipeline, so a request bound
/// is a bound on work that OBSERVES a token and on nothing else. IAuditWriter.WriteAsync
/// takes no token by design (D075) — an audit row that vanishes when a phone locks is not
/// an audit row — and the two put together mean the tier's real ceiling was the request
/// bound PLUS however long an uncancellable save felt like taking. With the retry policy
/// underneath it that was 260 + 230 seconds against a BFF that gave up at 300, so the
/// nesting D086 wrote down was false by the repository's own arithmetic.
///
/// THE FIX IS NOT A BIGGER CONSTANT. Deriving a larger request timeout leaves the tail
/// outside it; the tail is exactly the part the request bound cannot see. What is needed
/// is a second bound, owned by the writes themselves, that starts where the request bound
/// stops.
///
/// SO: NOT THE REQUEST'S TOKEN, AND NOT NO TOKEN. This is a per-request deadline that
///
///   * never fires while the request is alive and inside its own bound — so an audit row
///     written on an ordinary request is never at risk;
///   * gives every remaining uncancellable write <see cref="Grace"/> ONCE, in total, from
///     the moment the request bound fires — not per write, so a path with three of them
///     cannot spend three graces;
///   * caps itself at <paramref name="ceiling"/> from construction regardless, so a
///     request that is never bound at all (a background job, a test resolving the writer
///     directly) still cannot run forever.
///
/// The total is therefore <c>DatabaseTimeouts.Request + DatabaseTimeouts.UncancellableGrace
/// = DatabaseTimeouts.Ceiling</c>, BY CONSTRUCTION rather than by enumerating which writes
/// are reachable after cancellation. That distinction is the whole point: this repository's
/// recurring failure is an enumeration that was complete when it was written and stopped
/// being complete two commits later (D081's windows, D087's correction of it, D088's
/// migrations). A shared deadline does not care how many writes there are.
///
/// WHAT IT COSTS. An audit row CAN now be abandoned — if the database is still refusing
/// work <see cref="Grace"/> after a request has already blown its entire budget, the row
/// is lost where an unbounded write would have kept trying and might eventually have
/// landed. That is a real durability gap and it is the price of a ceiling anybody can
/// state. It is bounded in the other direction too: every path that writes an audit row
/// has already read from this database on the same request, so the write is never the
/// query carrying a resume from auto-pause — the resume is over by then.
///
/// AND THE SHARING HAS A SHARPER EDGE THAN "AN ABANDONED ROW", which is worth writing down
/// because it was found the expensive way. A grace shared first-come-first-served means a
/// write that STARTS after it is spent gets nothing at all — a cancelled token stays
/// cancelled, so <c>SaveChangesAsync</c> throws before issuing anything rather than being
/// merely short of time. So whichever uncancellable write runs FIRST on a path is the one
/// that survives, and the order they are written in is a control rather than a style
/// choice. That is invisible while audit writes are the only consumer and stops being
/// invisible the moment there is a second: <c>PracticeUserManager</c> puts Identity's store
/// calls here (they have no token of their own to observe), so a failed login has two
/// competing uncancellable writes, and <c>ProviderAuthenticator</c> audits before it
/// increments — see point 3 on that class.
///
/// WHAT IS DELIBERATELY NOT HERE. A transaction's BEGIN and COMMIT (<c>AtomicWrites</c>).
/// Not an oversight, and not fixable by handing them this token: SqlClient implements both
/// synchronously, so a token could only refuse to START one — and refusing to start a
/// COMMIT whose writes are already staged would roll back a decision that has already been
/// taken, which is the single failure <c>AtomicWrites</c> exists to prevent.
/// <c>DatabaseTimeouts.Ceiling</c> states that limit rather than claiming to cover it.
/// </summary>
public sealed class UncancellableWriteDeadline : IDisposable
{
    private readonly CancellationTokenSource _expiry = new();
    private readonly long _created = Stopwatch.GetTimestamp();
    private readonly TimeSpan _ceiling;
    private readonly TimeSpan _grace;

    private CancellationTokenRegistration _registration;
    private int _bound;

    /// <param name="ceiling">
    /// The hard cap from construction, for a scope nothing ever binds. In the application
    /// this is <c>DatabaseTimeouts.Ceiling</c>.
    /// </param>
    /// <param name="grace">
    /// What is left once the request bound has fired. In the application this is
    /// <c>DatabaseTimeouts.UncancellableGrace</c>.
    /// </param>
    public UncancellableWriteDeadline(TimeSpan ceiling, TimeSpan grace)
    {
        _ceiling = ceiling;
        _grace = grace;
        _expiry.CancelAfter(ceiling);
    }

    /// <summary>
    /// The token every uncancellable write runs on. Not the request's, and not
    /// <c>CancellationToken.None</c>.
    /// </summary>
    public CancellationToken Token => _expiry.Token;

    /// <summary>
    /// Starts the grace period when <paramref name="requestBound"/> fires.
    ///
    /// Called once per request, by ProviderContextMiddleware, which is the first
    /// application middleware and therefore runs before anything can write an audit row.
    /// The token it is handed is already the one RequestTimeouts substituted, so binding
    /// to it binds to the request bound as well as to a genuine client disconnect — both
    /// mean the same thing here: the caller is gone and the remaining work has
    /// <see cref="_grace"/> to finish.
    ///
    /// SECOND CALLS ARE IGNORED rather than throwing. A middleware that runs twice is a
    /// pipeline mistake, not a reason to fail a request, and re-registering would make the
    /// grace restartable — which is the one property this type must not have.
    /// </summary>
    public void BindTo(CancellationToken requestBound)
    {
        if (Interlocked.Exchange(ref _bound, 1) == 1) return;

        _registration = requestBound.Register(
            static state => ((UncancellableWriteDeadline)state!).StartGrace(), this);
    }

    /// <summary>
    /// The grace, or whatever is left of the hard cap — whichever is SHORTER.
    ///
    /// <c>CancelAfter</c> replaces the pending timer rather than shortening it, so a
    /// request that has already burned most of its ceiling must not be handed a fresh full
    /// grace by this call. The min is what makes <see cref="_ceiling"/> a cap and not a
    /// suggestion.
    /// </summary>
    private void StartGrace()
    {
        var remaining = _ceiling - Stopwatch.GetElapsedTime(_created);
        var allowance = remaining < _grace ? remaining : _grace;

        try
        {
            if (allowance <= TimeSpan.Zero) _expiry.Cancel();
            else _expiry.CancelAfter(allowance);
        }
        catch (ObjectDisposedException)
        {
            // The scope ended while the request token was firing. Nothing is waiting on
            // this deadline any more, so there is nothing to bound.
        }
    }

    public void Dispose()
    {
        _registration.Dispose();
        _expiry.Dispose();
    }
}
