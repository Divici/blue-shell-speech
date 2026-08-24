namespace Practice.Application.Providers;

/// <summary>
/// Who the current request belongs to.
///
/// Resolved from the validated caller, NEVER from a request body or query parameter,
/// however convenient that would be (docs/SECURITY.md). A provider id supplied by the
/// caller is a caller choosing whose records to read.
/// </summary>
public interface IProviderContext
{
    /// <summary>
    /// The current provider's internal id, or null when there is no authenticated caller.
    ///
    /// Null is not an error here — background jobs and health checks legitimately have no
    /// provider. It IS an error for anything that touches patient data, which is why the
    /// query filter treats null as "match nothing" rather than "match everything".
    /// </summary>
    long? ProviderId { get; }
}

/// <summary>
/// A fixed provider, for background work and tests.
/// </summary>
public sealed class FixedProviderContext(long? providerId) : IProviderContext
{
    public long? ProviderId { get; } = providerId;
}
