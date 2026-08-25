using Microsoft.Extensions.Logging;
using Practice.Application.Consultations;

namespace Practice.Infrastructure.Notifications;

/// <summary>
/// The whole notification, as constants.
///
/// It is not composed from anything. There is no interpolation, no format string, and no
/// argument — which is what makes "the notification carries no content" a property of the
/// code rather than a promise about it (CLAUDE.md, docs/DATA_MODEL.md).
/// </summary>
public static class ConsultationNotification
{
    public const string Subject = "New consultation request";

    /// <summary>The exact sentence CLAUDE.md specifies. Nothing may be appended to it.</summary>
    public const string Body = "New consultation request, sign in to view.";
}

/// <summary>
/// Records the notification. Does not yet send it.
///
/// WHAT IS MISSING AND WHY, stated rather than implied: there is no mail transport. The
/// practice has no mailbox — "real practice phone and email" is an open item on
/// WORK_QUEUE.md's Blocked list, and Azure Communication Services needs a verified sender
/// domain, which needs the domain purchase that is blocked with it. Writing a transport
/// against a placeholder address would produce a delivery path nobody has ever seen work,
/// which is the failure mode this codebase keeps catching: a control that reads as present
/// and is not.
///
/// So the seam is real, the contentlessness is enforced by the seam's own signature, and
/// the delivery step is one class away. What exists today is a structured log line, which
/// means an arrival is at least observable, and the enquiry itself is durable in
/// ConsultationRequests whether or not anyone is told about it.
///
/// The log line carries the SAME thing the email will: an opaque public id and a fixed
/// sentence. Logs are the other channel that leaves this system (docs/SECURITY.md), so the
/// rule is identical on both.
/// </summary>
public sealed partial class LoggingConsultationNotifier(
    ILogger<LoggingConsultationNotifier> logger) : IConsultationNotifier
{
    public Task NotifyAsync(Guid consultationRequestPublicId)
    {
        LogConsultationRequestReceived(
            logger, consultationRequestPublicId, ConsultationNotification.Body);

        return Task.CompletedTask;
    }

    /*
     * Source-generated, as required by CA1848 and worth having anyway: a strongly typed
     * call site cannot accidentally interpolate a parent's words into a log message. Both
     * parameters here are non-content by construction — an opaque id, and a constant.
     */
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Consultation request {ConsultationRequestPublicId} received. Notification: {NotificationBody}")]
    private static partial void LogConsultationRequestReceived(
        ILogger logger, Guid consultationRequestPublicId, string notificationBody);
}
