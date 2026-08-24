namespace Practice.Domain.Auditing;

public enum AuditOutcome
{
    Success = 1,
    Failure = 2,

    /// <summary>
    /// The actor was authenticated but not permitted.
    ///
    /// Distinct from Failure because a burst of these is a very different signal from a
    /// burst of failed logins — it means someone with valid credentials is reaching for
    /// records that are not theirs.
    /// </summary>
    Denied = 3,
}
