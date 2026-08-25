namespace Practice.Application.Consultations;

/// <summary>
/// Tells Michelle that a consultation request has arrived — and NOTHING ELSE ABOUT IT.
///
/// THE SIGNATURE IS THE CONTROL, NOT A COMMENT ASKING FOR CARE.
///
/// CLAUDE.md and docs/DATA_MODEL.md require the notification to be contentless: "New
/// consultation request, sign in to view." The obvious way to build that is a method
/// taking the enquiry and a docstring saying not to include the fields — which is a rule
/// that holds until the first person who wants the child's name in the subject line. So
/// this method is handed an OPAQUE IDENTIFIER and no content at all: there is no parameter
/// through which a parent's description of their child could travel, whatever a future
/// implementation or a future caller intends.
///
/// Why it matters more here than for most notifications: email is not a channel we
/// control. It sits in a mailbox on somebody else's infrastructure, gets forwarded to a
/// phone, is read on a train, and survives every retention policy this application has. A
/// child's first name next to a list of developmental concerns arriving there is a
/// disclosure that no amount of care inside this system can undo.
///
/// NO CancellationToken, for the same reason IAuditWriter has none (D075). The row is
/// already committed by the time this runs; the parent's browser going away does not
/// un-submit their enquiry, and a notification abandoned mid-flight would leave a request
/// sitting unread with nothing to say it had arrived. CA2016 is an error in this project,
/// so a token parameter would force every call site holding one to forward it — the
/// analyser would enforce the defect. Leaving nothing to forward is the only version of
/// this the toolchain agrees with.
/// </summary>
public interface IConsultationNotifier
{
    /// <param name="consultationRequestPublicId">
    /// The opaque public id, so a log line or a future message can be correlated with the
    /// row. Never the clustered key, and never anything a parent typed.
    /// </param>
    Task NotifyAsync(Guid consultationRequestPublicId);
}
