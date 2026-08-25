using System.Reflection;
using Microsoft.Extensions.Logging;
using Practice.Application.Consultations;
using Practice.Infrastructure.Notifications;

namespace Practice.Api.Tests;

/// <summary>
/// The contentless notification, asserted at the seam rather than at the call site.
///
/// CLAUDE.md and docs/DATA_MODEL.md require the email to say "New consultation request,
/// sign in to view" and nothing else. The usual way that requirement is met is a docstring
/// asking the next person to be careful, which holds until somebody wants the child's name
/// in the subject line so the inbox is easier to scan. These tests assert the version that
/// does not depend on anyone remembering: THERE IS NO PARAMETER THROUGH WHICH CONTENT
/// COULD TRAVEL.
///
/// No database and no HTTP — this is a property of the types.
/// </summary>
public sealed class ConsultationNotifierTests
{
    /*
     * SYNTHETIC MARKERS. Distinctive on purpose: "this string appears nowhere in the
     * notification" gives a false pass if the string could occur by accident.
     */
    private const string ChildMarker = "Vexlimund";
    private const string ConcernMarker = "Qwintaxel, ten words, no combinations";

    /// <summary>
    /// Everything the seam is allowed to carry.
    ///
    /// A Guid is opaque — it identifies a row and describes nothing about the family in
    /// it. Anything else is a channel: a string could be a name, an int could be an age,
    /// and a domain type carries the lot.
    /// </summary>
    private static readonly Type[] OpaqueTypes = [typeof(Guid)];

    /// <summary>
    /// The notification interface can carry no content, by construction.
    ///
    /// Control: the parameter list of IConsultationNotifier.NotifyAsync — that it takes a
    /// Guid and nothing else.
    /// Falsified by adding <c>string childFirstName</c> to it — the inverse of a deletion,
    /// because the control here is an ABSENCE and there is nothing to remove. Every
    /// implementation had to be widened alongside it for the build to survive, which is
    /// itself the argument: a content parameter cannot be added quietly.
    /// → red, "IConsultationNotifier must be unable to carry anything a parent typed.
    /// Found: NotifyAsync(String childFirstName)".
    /// </summary>
    [Fact]
    public void The_notification_seam_can_carry_no_content()
    {
        var offending = typeof(IConsultationNotifier)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(method => method.GetParameters()
                .Where(p => !OpaqueTypes.Contains(p.ParameterType))
                .Select(p => $"{method.Name}({p.ParameterType.Name} {p.Name})"))
            .ToArray();

        Assert.True(
            offending.Length == 0,
            "IConsultationNotifier must be unable to carry anything a parent typed. Found: "
                + string.Join(", ", offending));
    }

    /// <summary>
    /// The sentence is the one CLAUDE.md specifies, exactly.
    ///
    /// Pinned as a literal rather than compared to a constant, so that changing the
    /// constant is a change to this test as well — which is the point. A parent's
    /// description of their child in a plaintext inbox is a disclosure, and the wording is
    /// a compliance decision rather than copy.
    ///
    /// Control: ConsultationNotification.Body. Deleting a constant only breaks the build,
    /// so it was falsified the way it would actually go wrong — by widening the sentence to
    /// "New consultation request about Vexlimund, sign in to view."
    /// → red, "Assert.Equal() Failure: Strings differ, Expected: ···ew consultation
    /// request, sign in to view., Actual: ···consultation request about Vexlimund, sig···".
    /// </summary>
    [Fact]
    public void The_notification_body_is_the_exact_sentence_and_names_nobody()
    {
        Assert.Equal(
            "New consultation request, sign in to view.", ConsultationNotification.Body);

        Assert.DoesNotContain(ChildMarker, ConsultationNotification.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(ConcernMarker, ConsultationNotification.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// What the notifier writes to the log is the same contentless pair the email will be.
    ///
    /// Logs are the other channel that leaves this system (docs/SECURITY.md non-negotiable
    /// #3), so a notifier that kept the email clean and described the enquiry in a log line
    /// would have moved the disclosure rather than prevented it.
    ///
    /// Control: the {NotificationBody} argument of LogConsultationRequestReceived — that it
    /// is ConsultationNotification.Body and not something built from a submission.
    /// Replaced with the literal "New request about Vexlimund" → red,
    /// "Assert.DoesNotContain() Failure: Sub-string found".
    /// </summary>
    [Fact]
    public async Task The_logged_notification_describes_nothing_about_the_child()
    {
        var log = new CapturingLogger();
        var notifier = new LoggingConsultationNotifier(log);

        await notifier.NotifyAsync(Guid.NewGuid());

        var written = Assert.Single(log.Messages);
        Assert.DoesNotContain(ChildMarker, written, StringComparison.Ordinal);
        Assert.DoesNotContain(ConcernMarker, written, StringComparison.Ordinal);
        Assert.Contains(ConsultationNotification.Body, written, StringComparison.Ordinal);
    }

    /// <summary>
    /// Records the FORMATTED message, which is what a sink receives.
    ///
    /// Asserting on the state object instead would miss an argument interpolated into the
    /// template, which is precisely the shape of the leak being guarded against.
    /// </summary>
    private sealed class CapturingLogger : ILogger<LoggingConsultationNotifier>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
