namespace Practice.Domain.Auditing;

/// <summary>
/// The auditable events, from docs/SECURITY.md.
///
/// Explicit values, never reordered: these are persisted as integers, and renumbering
/// would silently rewrite the meaning of every historical row.
/// </summary>
public enum AuditEventType
{
    LoginSucceeded = 1,
    LoginFailed = 2,
    MfaChallenged = 3,
    MfaEnrolled = 4,
    RecoveryCodeUsed = 5,
    LoggedOut = 6,

    /// <summary>Read access to a patient record. Auditable under HIPAA, not just writes.</summary>
    PatientViewed = 20,
    PatientCreated = 21,
    PatientUpdated = 22,

    NoteSigned = 40,
    NoteAmended = 41,

    /// <summary>
    /// An empty, unsigned draft was removed — the only row this application ever deletes
    /// from the clinical tables, and therefore the one that most needs a record.
    /// </summary>
    NoteDiscarded = 42,

    AudioDeleted = 60,
    ExportGenerated = 61,
}
