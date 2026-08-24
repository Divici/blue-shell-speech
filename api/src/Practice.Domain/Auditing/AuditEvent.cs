using Practice.Domain.Common;

namespace Practice.Domain.Auditing;

/// <summary>
/// What happened, when, and to which record.
///
/// Append-only. The application's SQL principal is granted no UPDATE or DELETE on this
/// table (docs/SECURITY.md) — a breach nobody can scope is worse than a breach.
///
/// READS ARE AUDITED, not only writes. Under HIPAA, access to ePHI is an auditable event;
/// most homegrown systems log only mutations and discover the gap during an investigation.
/// </summary>
public sealed class AuditEvent : Entity
{
    private AuditEvent() { }

    public DateTime OccurredAtUtc { get; private set; }

    /// <summary>Null for events that happen before a provider is known, e.g. a failed login.</summary>
    public long? ProviderId { get; private set; }

    /// <summary>Identity user id, when there is one.</summary>
    public string? ActorUserId { get; private set; }

    public AuditEventType EventType { get; private set; }

    /// <summary>e.g. "Patient", "ClinicalNote". Null for events with no subject.</summary>
    public string? EntityType { get; private set; }

    /// <summary>The PUBLIC id of the subject. Never the clustered key.</summary>
    public Guid? EntityPublicId { get; private set; }

    public string? CorrelationId { get; private set; }

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public AuditOutcome Outcome { get; private set; }

    /// <summary>
    /// Structured detail — IDs and reasons only.
    ///
    /// MUST NEVER CONTAIN CLINICAL CONTENT. The audit log is the table most likely to be
    /// exported, shipped to a SIEM, or read by a third party during an investigation, so
    /// PHI here multiplies the blast radius of every one of those.
    /// </summary>
    public string? Metadata { get; private set; }

    public static AuditEvent Record(
        AuditEventType eventType,
        AuditOutcome outcome,
        string? actorUserId = null,
        long? providerId = null,
        string? entityType = null,
        Guid? entityPublicId = null,
        string? correlationId = null,
        string? ipAddress = null,
        string? userAgent = null,
        string? metadata = null) =>
        new()
        {
            OccurredAtUtc = DateTime.UtcNow,
            EventType = eventType,
            Outcome = outcome,
            ActorUserId = actorUserId,
            ProviderId = providerId,
            EntityType = entityType,
            EntityPublicId = entityPublicId,
            CorrelationId = correlationId,
            IpAddress = ipAddress,
            // Bounded: a hostile client can send a very long User-Agent, and an audit row
            // is not the place to discover that.
            UserAgent = userAgent?.Length > 512 ? userAgent[..512] : userAgent,
            Metadata = metadata,
        };
}
