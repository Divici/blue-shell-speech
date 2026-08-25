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

    /// <summary>
    /// A consultation request arrived from the public form.
    ///
    /// The only write in this system performed by an UNAUTHENTICATED caller, which is
    /// precisely why it is audited: there is no session, no actor id, and no clinician to
    /// ask afterwards. A burst of these is the signal that the public form is being used
    /// as a cost-amplification vector against containers that scale from zero, and the
    /// audit table is the only place that burst leaves a mark (docs/SECURITY.md §Audit).
    ///
    /// Adding an entity is not audited by default — the guardian write shipped writing
    /// nothing (D073) — so this exists because it was written, not because it was implied.
    /// </summary>
    ConsultationRequestReceived = 80,

    /// <summary>
    /// The enquiry was stored but Michelle could not be told about it.
    ///
    /// Its own event rather than a Failure outcome on the arrival: the request DID arrive,
    /// and a row saying otherwise would be counted a year later by somebody who was not
    /// here. A notifier that silently fails looks exactly like one that works.
    /// </summary>
    ConsultationNotificationFailed = 81,

    /// <summary>
    /// Michelle read an enquiry, or the inbox listing them.
    ///
    /// ITS OWN EVENT RATHER THAN PatientViewed, which is the one place this vocabulary is
    /// allowed to grow (D076 argues against growing it at all). The family who sent an
    /// enquiry are NOT patients — there is no treatment relationship, which is the whole
    /// reason ConsultationRequest's docstring calls the row PHI-adjacent rather than PHI —
    /// so recording their enquiry as a patient record being viewed would inflate the count
    /// of "who accessed a child's medical record" by exactly the set of people who have
    /// never been treated here. That count is read once, years later, by somebody who was
    /// not here, and it has to mean one thing.
    ///
    /// Written on both endpoints that disclose an enquiry, because an endpoint has to be
    /// safe on its own terms rather than because of who currently calls it (D065). The
    /// metadata says which read happened and how much came back.
    /// </summary>
    ConsultationRequestViewed = 82,

    /// <summary>
    /// An enquiry moved: contacted, converted into a patient, or declined.
    ///
    /// Mirrors PatientUpdated deliberately. `action=` carries a fixed vocabulary, and the
    /// conversion also carries the new patient's public id — that link is the answer to
    /// "where did this family come from", and it is a Guid because nothing patient-facing
    /// in this system is a sequential integer (CLAUDE.md conventions).
    /// </summary>
    ConsultationRequestUpdated = 83,

    /// <summary>
    /// A request was refused by the rate limiter before the work it asked for was done.
    ///
    /// ITS OWN EVENT RATHER THAN A Denied OUTCOME ON LoginFailed, and numbered here rather
    /// than beside the login rows because it is cross-cutting: the same event covers every
    /// limited path, and `policy=` says which. A refused request is not a failed credential
    /// attempt — nothing was checked, no password was hashed, and no account was involved —
    /// so recording it as one would inflate "failed logins" by exactly the set of requests
    /// that never reached a login. That count is read once, years later, by somebody who was
    /// not here, and it has to mean one thing (the argument D076 makes about growing this
    /// vocabulary at all, and the one ConsultationRequestViewed makes about not reusing
    /// PatientViewed).
    ///
    /// The metadata says which DIMENSION tripped — a flood from one place, or a flood at one
    /// submitted address. It carries no address and no submitted identity: a table of the
    /// addresses somebody guessed is the enumeration list this control exists to deny, in
    /// the one table this application never deletes from (docs/SECURITY.md §Audit).
    ///
    /// WRITTEN ONCE PER PARTITION PER WINDOW, not once per refused request. A row per
    /// request would make the audit table the amplification target the limiter exists to
    /// close — see IRateLimitStore for what that buys and what it costs.
    /// </summary>
    RateLimited = 90,
}
