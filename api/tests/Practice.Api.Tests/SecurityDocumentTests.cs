using System.Text.RegularExpressions;
using Practice.Domain.Auditing;

namespace Practice.Api.Tests;

/// <summary>
/// The compliance documents, held to the code.
///
/// THIS IS D072's CLASS AND IT HAS NOW BEEN FOUND SEVEN TIMES: a control described in prose, in
/// a file nothing compiles, with nothing able to notice it going stale. Two consecutive sweeps
/// missed false claims in the SAME paragraph they were correcting. Prose cannot be made to
/// review itself, but the claims with a machine-readable shape can be pinned, and these are the
/// two that drift on their own — a list of event types, and a control whose whole status is
/// "not built yet".
///
/// No database. These read two source trees and compare them, like
/// <c>RequestBoundsTests.The_bff_waits_longer_than_this_api_is_prepared_to_spend</c>, which is
/// the only shape of test that has ever caught this class here.
/// </summary>
public sealed class SecurityDocumentTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);

    /// <summary>
    /// docs/SECURITY.md §Audit lists exactly the events the code writes, and exactly the ones
    /// it does not.
    ///
    /// THE LIST WAS WRONG IN BOTH DIRECTIONS. It named <c>AudioDeleted</c> and
    /// <c>ExportGenerated</c> — declared in the enum, written by nothing — under the word
    /// "Recorded", so two events nobody emits read as audit controls; and it omitted
    /// <c>MfaEnrolled</c>, <c>RecoveryCodeUsed</c>, <c>PatientCreated</c> and
    /// <c>PatientUpdated</c>, which are written on every enrolment, every recovery sign-in and
    /// every guardian change. A reader deciding whether this system can answer "who saw this
    /// record" was reading a list assembled by hand once.
    ///
    /// The set comes from the tree, not from a list in this file (D090, "glob, do not
    /// enumerate"): every <c>AuditEventType.X</c> reference under <c>api/src</c>, minus the
    /// enum's own declaration file. It matches REFERENCES rather than proven emissions, which
    /// errs toward demanding that the document mention a type — the safe direction, since the
    /// failure this closes is a document that is quieter than the code.
    ///
    /// Control: the <c>**Emitted today:**</c> list in docs/SECURITY.md §Audit.
    /// <c>`RecoveryCodeUsed`</c> removed from it → red, "docs/SECURITY.md §Audit does not
    /// name event(s) this application writes: RecoveryCodeUsed." That is the direction the
    /// document has never failed in; the direction it HAS failed in is the second
    /// assertion, which is what `AudioDeleted` and `ExportGenerated` were doing in a list
    /// headed "Recorded".
    /// </summary>
    [Fact]
    public void The_security_document_lists_the_audit_events_the_code_actually_writes()
    {
        var referenced = Directory
            .EnumerateFiles(RepoTree.File("api/src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "AuditEventType.cs")
            .SelectMany(path => Regex
                .Matches(File.ReadAllText(path), @"AuditEventType\.(\w+)", RegexOptions.None, Patience)
                .Select(match => match.Groups[1].Value))
            .Where(name => Enum.IsDefined(typeof(AuditEventType), name))
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var security = File.ReadAllText(RepoTree.File("docs/SECURITY.md"));

        var emitted = NamesIn(security, @"\*\*Emitted today:\*\*(.+?)\r?\n\r?\n");
        var dormant = NamesIn(security, @"\*\*Declared in `AuditEventType`[^*]*\*\*(.+?)\r?\n\r?\n");

        var undocumented = referenced.Except(emitted, StringComparer.Ordinal).ToArray();

        Assert.True(
            undocumented.Length == 0,
            "docs/SECURITY.md §Audit does not name event(s) this application writes: "
            + string.Join(", ", undocumented)
            + ". That paragraph is what a compliance reviewer reads to decide whether an "
            + "access question can be answered after the fact, and a shorter list than the "
            + "code's understates the trail rather than overstating it — which is the one "
            + "direction this file has never failed in before.");

        var claimed = emitted.Except(referenced, StringComparer.Ordinal).ToArray();

        Assert.True(
            claimed.Length == 0,
            "docs/SECURITY.md §Audit lists event(s) nothing under api/src ever writes: "
            + string.Join(", ", claimed)
            + ". An event type declared and never emitted reads as a control; it is an enum "
            + "member. Move it to the 'not yet written by anything' list with its task number "
            + "rather than deleting it — the intent is worth keeping and the claim is not.");

        var stillDormant = dormant.Intersect(referenced, StringComparer.Ordinal).ToArray();

        Assert.True(
            stillDormant.Length == 0,
            "docs/SECURITY.md §Audit still calls " + string.Join(", ", stillDormant)
            + " unwritten, and the code now writes it. Move it up to the emitted list: the "
            + "half of this that goes stale quietly is the half that says a control is "
            + "missing after somebody has built it.");
    }

    /// <summary>
    /// Both documents describe login rate limiting the way the code leaves it — and the
    /// marker is the SHARED STORE, not the word "limiter".
    ///
    /// WHY THIS ONE IS PINNED AND THE OTHER PLANNED CONTROLS ARE NOT. It began as a pin on a
    /// gap: the five-failure lockout only ever counts attempts against an address that HAS an
    /// account, so a stream of guesses at random addresses was limited by nothing at all, and
    /// the failure mode being guarded was the gap becoming invisible because a document sweep
    /// tidied the sentence away. WORK_QUEUE 1.19 built the limiter, so the pin now guards the
    /// other direction — and the direction it guards has to be the one that could go wrong.
    ///
    /// SO THE MARKER MOVED, FROM <c>AddRateLimiter</c> TO <c>RateLimitCounters</c>. The first
    /// was a guess at an implementation and this one is not: an in-process limiter would
    /// satisfy "a limiter exists" while being exactly the control 1.18 refused to ship — one
    /// replica's worth of counting on a container app that scales horizontally and to zero.
    /// The table name is what distinguishes the thing that was built from the thing that was
    /// rejected, so it is what both documents have to be able to say and what the tree has to
    /// contain.
    ///
    /// IT STILL FAILS IN BOTH DIRECTIONS. Drop the store and leave the documents claiming it
    /// → red. Rewrite the rows without it, or let them drift back to "planned", while the
    /// store is there → red. Same two-tree shape as the BFF timeout guard.
    ///
    /// Control: the "Rate limiting on login" row in docs/SECURITY.md §Authentication and the
    /// boundary ① S row in docs/THREAT_MODEL.md.
    /// The word <c>RateLimitCounters</c> removed from the SECURITY.md row, leaving the row
    /// otherwise intact and still describing a limiter — the plausible tidy-up, since nobody
    /// deletes a whole row — → red, "docs/SECURITY.md describes login rate limiting without
    /// naming the shared store, and api/src has one (RateLimitCounters)."
    ///
    /// Control: the same, in the other direction — the phrase <c>planned — 1.19</c> restored
    /// to the THREAT_MODEL.md row while the code has a limiter → red, "docs/THREAT_MODEL.md
    /// still calls login rate limiting planned (1.19 / 4.3), and api registers one."
    /// </summary>
    [Fact]
    public void Both_documents_describe_login_rate_limiting_as_the_code_leaves_it()
    {
        /*
         * THE SHARED STORE, BY NAME.
         *
         * "Is there a limiter" is the question this used to ask and it is the wrong one: an
         * in-process fixed window answers yes and limits one replica. The counter table is
         * the difference between the control that was built and the one WORK_QUEUE 1.18
         * declined to ship, so it is what the documents are held to.
         */
        var store = Directory
            .EnumerateFiles(RepoTree.File("api/src"), "*.cs", SearchOption.AllDirectories)
            .Any(path => File.ReadAllText(path)
                .Contains("RateLimitCounters", StringComparison.Ordinal));

        var documents = new[] { "docs/SECURITY.md", "docs/THREAT_MODEL.md" };

        foreach (var relative in documents)
        {
            var text = File.ReadAllText(RepoTree.File(relative));

            // Both task numbers are accepted: 4.3 was pulled forward to 1.19 while the pin
            // was being written, and the claim under test was never which slot in the queue
            // the work occupied.
            var stillPlanned = Regex.IsMatch(
                text, @"[Pp]lanned — (WORK_QUEUE )?(4\.3|1\.19)", RegexOptions.None, Patience);

            Assert.False(
                stillPlanned && store,
                $"{relative} still calls login rate limiting planned (1.19 / 4.3), and api "
                + "registers one. A document that understates a control is less dangerous "
                + "than one that overstates it and is still wrong — the next reviewer judges "
                + "the code against this file (CLAUDE.md).");

            var describes = text.Contains("RateLimitCounters", StringComparison.Ordinal);

            Assert.True(
                describes == store,
                store
                    ? $"{relative} describes login rate limiting without naming the shared "
                      + "store, and api/src has one (RateLimitCounters). The whole reason "
                      + "1.18 refused to ship a limiter is that an in-process one counts a "
                      + "single replica on a container app that scales horizontally and to "
                      + "zero — so a reader cannot tell which of the two this is from a row "
                      + "that only says \"rate limited\"."
                    : $"{relative} names RateLimitCounters and nothing under api/src does. "
                      + "The store is gone, or was renamed, and the compliance documents "
                      + "still claim a control that holds across replicas. Say what is "
                      + "actually there, with a task number if it is planned.");
        }
    }

    /// <summary>
    /// Backticked identifiers inside the block a pattern captures, in document order.
    /// </summary>
    private static string[] NamesIn(string document, string pattern)
    {
        var block = Regex.Match(document, pattern, RegexOptions.Singleline, Patience);

        Assert.True(
            block.Success,
            "docs/SECURITY.md §Audit no longer carries the paragraph this guard reads "
            + $"(/{pattern}/). The list of audited events is a compliance claim and nothing "
            + "else relates it to the code.");

        return [.. Regex
            .Matches(block.Groups[1].Value, @"`(\w+)`", RegexOptions.None, Patience)
            .Select(match => match.Groups[1].Value)
            .Where(name => Enum.IsDefined(typeof(AuditEventType), name))
            .Distinct()];
    }
}
