namespace Practice.Infrastructure.Persistence;

/// <summary>
/// How long this application is prepared to wait on the database, in one place.
///
/// These numbers were previously nowhere. <c>AddInfrastructure</c> set no command timeout
/// at all, so the bound was ADO.NET's 30-second default — which is not a decision, is not
/// reviewable next to the 180 the design-time factory sets, and can be silently replaced
/// by a <c>Command Timeout</c> keyword in a connection string this application does not
/// own. A default that happens to be reasonable still leaves nobody able to say what the
/// bound is, and <see cref="Practice.Infrastructure.Identity.IAuditWriter"/>'s own
/// docstring was already naming a command timeout no configuration set (D072's class).
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
    /// NOT the ceiling on a request. <c>EnableRetryOnFailure</c> allows five retries with
    /// up to ten seconds between them, so a wedged database bounds a retried operation at
    /// roughly six commands plus the backoff, not at thirty seconds. That is the number
    /// the audit writer's docstring has to name, because an audit write does not observe
    /// the request token and nothing else stops it.
    /// </summary>
    public const int CommandSeconds = 30;

    /// <summary>
    /// The ceiling on a whole request, for the work that can be abandoned.
    ///
    /// Thirty seconds because the BFF gives up at twenty-five (<c>web/lib/api</c>), so a
    /// request still running at thirty has no caller left: the parent or the clinician saw
    /// an error some seconds ago. Holding the request and its pooled connection past that
    /// point serves nobody and costs a connection on a container that scales to zero.
    ///
    /// It bounds only what observes <c>HttpContext.RequestAborted</c>. Audit writes
    /// deliberately do not (D075) — an audit row that disappears when the caller walks
    /// away is not an audit row — so this is a bound on reads and on the transaction body,
    /// never on the record that something was attempted.
    /// </summary>
    public static readonly TimeSpan Request = TimeSpan.FromSeconds(30);
}
