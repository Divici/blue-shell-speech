using System.Security.Cryptography;
using System.Text;
using Practice.Domain.Common;

namespace Practice.Domain.ClinicalNotes;

/// <summary>
/// A SOAP note.
///
/// THE CENTRAL RULE OF THIS SYSTEM: a signed note is never modified. Ever.
///
/// An amendment INSERTS a new version pointing at the one it supersedes. The original row
/// keeps its content, its signature, and its content hash forever. Nothing overwrites a
/// signed clinical record — not an edit, not a migration, not a script run at 11pm.
///
/// This class enforces that in the domain. The database enforces it independently, with a
/// filtered unique index, a CHECK constraint, and an UPDATE trigger (docs/DATA_MODEL.md).
/// Application-layer immutability holds right up until someone opens SSMS.
/// </summary>
public sealed class ClinicalNote : Entity
{
    private ClinicalNote() { }

    public long ProviderId { get; private set; }

    public long PatientId { get; private set; }

    public long AppointmentId { get; private set; }

    /// <summary>1 for the original, incrementing with each amendment.</summary>
    public int VersionNumber { get; private set; } = 1;

    /// <summary>The note this one replaces. Null on the original.</summary>
    public long? SupersedesNoteId { get; private set; }

    /// <summary>
    /// Exactly one note per appointment carries this.
    ///
    /// Enforced by a filtered unique index, so two current notes for one visit is
    /// impossible rather than merely unlikely.
    /// </summary>
    public bool IsCurrent { get; private set; } = true;

    public NoteStatus Status { get; private set; } = NoteStatus.Draft;

    public string Subjective { get; private set; } = string.Empty;

    public string Objective { get; private set; } = string.Empty;

    public string Assessment { get; private set; } = string.Empty;

    public string Plan { get; private set; } = string.Empty;

    /// <summary>Whether a human wrote this or reviewed a generated draft.</summary>
    public NoteOrigin Origin { get; private set; } = NoteOrigin.Manual;

    public DateTime? SignedAtUtc { get; private set; }

    public string? SignedBy { get; private set; }

    /// <summary>Required on an amendment. Why the record needed correcting.</summary>
    public string? AmendmentReason { get; private set; }

    /// <summary>
    /// SHA-256 of the four SOAP fields, computed at signature.
    ///
    /// Makes tampering DETECTABLE rather than merely prohibited. If a row is altered by
    /// any route that bypasses this code, the hash no longer matches the content — which
    /// is the difference between "we believe the record is intact" and "we can show it".
    /// </summary>
    public byte[]? ContentHash { get; private set; }

    /// <summary>
    /// Whether this row can be discarded outright rather than kept forever.
    ///
    /// TRUE ONLY for an unsigned draft with nothing in any of the four sections. The rule
    /// that a signed note is never modified is untouched by this: an empty draft attests
    /// to nothing and documents nothing. Keeping it leaves a permanent "Draft" badge on a
    /// visit that was never documented, clearable only by writing content onto that
    /// child's chart and signing it into immutability — which is a worse outcome than the
    /// mis-tap that created it.
    ///
    /// FALSE FOR AN AMENDMENT, whatever its content. That clause is not redundant with
    /// the emptiness ones and an earlier version of this predicate went without it.
    ///
    /// Amend() marks the version it supersedes Amended with IsCurrent = 0 before the new
    /// row exists, so the amendment IS the visit's current note. It also starts as a
    /// Draft, and UpdateContent edits drafts freely — so clearing all four sections is an
    /// ordinary supported call, after which every emptiness clause below is satisfied.
    /// Deleting that row leaves the visit with no current note at all: the schedule offers
    /// to start a fresh one, GET /notes/appointment/{visit} answers 404, and the signed
    /// version underneath is reachable by nothing the product renders.
    ///
    /// The distinction the rest of the predicate draws is "does this row record anything".
    /// An amendment records something even when blank — that a signed note was superseded.
    /// </summary>
    public bool CanBeDiscarded =>
        Status == NoteStatus.Draft
        && SupersedesNoteId is null
        && string.IsNullOrWhiteSpace(Subjective)
        && string.IsNullOrWhiteSpace(Objective)
        && string.IsNullOrWhiteSpace(Assessment)
        && string.IsNullOrWhiteSpace(Plan);

    public static ClinicalNote CreateDraft(
        long providerId,
        long patientId,
        long appointmentId,
        NoteOrigin origin = NoteOrigin.Manual)
    {
        if (providerId <= 0) throw new ArgumentException("A note needs a provider.", nameof(providerId));
        if (patientId <= 0) throw new ArgumentException("A note needs a patient.", nameof(patientId));
        if (appointmentId <= 0) throw new ArgumentException("A note needs a visit.", nameof(appointmentId));

        return new ClinicalNote
        {
            ProviderId = providerId,
            PatientId = patientId,
            AppointmentId = appointmentId,
            Origin = origin,
        };
    }

    /// <summary>
    /// Edits the note. Permitted ONLY while it is a draft.
    /// </summary>
    public void UpdateContent(string subjective, string objective, string assessment, string plan)
    {
        if (Status != NoteStatus.Draft)
        {
            /*
             * Two refusals, because "create an amendment instead" is only true of one of
             * them. Amend() rejects a version that has already been superseded, so the
             * single sentence sent whoever was reading a superseded v1 to an action that
             * answered with a second refusal. The endpoint returns this wording verbatim
             * to the clinician (NoteEndpoints.UpdateDraft), so a refusal that names an
             * impossible next step is a screen telling her something untrue.
             */
            throw new InvalidOperationException(Status == NoteStatus.Amended
                ? "This version was signed and has since been replaced by a later one. It is kept exactly as it was — corrections go on the current version."
                : "This note is signed. Create an amendment instead — a signed clinical record is never edited.");
        }

        Subjective = Trim(subjective);
        Objective = Trim(objective);
        Assessment = Trim(assessment);
        Plan = Trim(plan);
    }

    /// <summary>
    /// Signs the note.
    ///
    /// From here the content is fixed. The hash is computed now, over exactly the four
    /// fields as they stand.
    /// </summary>
    public void Sign(string signedBy, DateTime signedAtUtc)
    {
        if (Status != NoteStatus.Draft)
        {
            throw new InvalidOperationException("This note has already been signed.");
        }

        if (signedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Signature time must be UTC.", nameof(signedAtUtc));
        }

        /*
         * An empty note cannot be signed.
         *
         * A signature is an attestation. Attesting to nothing is worse than having no note:
         * it creates a record that says a visit was documented when it was not.
         */
        if (string.IsNullOrWhiteSpace(Subjective)
            && string.IsNullOrWhiteSpace(Objective)
            && string.IsNullOrWhiteSpace(Assessment)
            && string.IsNullOrWhiteSpace(Plan))
        {
            throw new InvalidOperationException("An empty note cannot be signed.");
        }

        Status = NoteStatus.Signed;
        SignedBy = Guard.NotBlank(signedBy, "signedBy");
        SignedAtUtc = signedAtUtc;
        ContentHash = ComputeContentHash();
    }

    /// <summary>
    /// Creates the next version of this note.
    ///
    /// Returns a NEW entity. This one is marked superseded and keeps every byte of its
    /// content — that is the whole point.
    /// </summary>
    public ClinicalNote Amend(string reason)
    {
        if (Status == NoteStatus.Draft)
        {
            throw new InvalidOperationException(
                "A draft has not been signed yet — edit it rather than amending it.");
        }

        if (!IsCurrent)
        {
            throw new InvalidOperationException(
                "This version has already been superseded. Amend the current one.");
        }

        var amendmentReason = Guard.MaxLength(
            Guard.NotBlank(reason, "reason"), 500, "reason");

        // The previous version stops being current. Its content is untouched.
        IsCurrent = false;
        Status = NoteStatus.Amended;

        return new ClinicalNote
        {
            ProviderId = ProviderId,
            PatientId = PatientId,
            AppointmentId = AppointmentId,
            VersionNumber = VersionNumber + 1,
            SupersedesNoteId = Id,
            IsCurrent = true,
            Status = NoteStatus.Draft,
            Origin = Origin,
            AmendmentReason = amendmentReason,

            // The amendment starts as a copy, so the clinician corrects rather than retypes.
            Subjective = Subjective,
            Objective = Objective,
            Assessment = Assessment,
            Plan = Plan,
        };
    }

    /// <summary>
    /// Verifies the stored hash against the current content.
    ///
    /// Returns false if the row was altered outside this type — the check that turns
    /// "immutable by policy" into "provably unmodified".
    /// </summary>
    public bool VerifyIntegrity()
    {
        if (ContentHash is null) return Status == NoteStatus.Draft;
        return CryptographicOperations.FixedTimeEquals(ContentHash, ComputeContentHash());
    }

    private byte[] ComputeContentHash()
    {
        /*
         * Field separators are part of the hash input.
         *
         * Without them, moving text from the end of Subjective to the start of Objective
         * would produce the same hash — a rearrangement that changes clinical meaning
         * while appearing untouched.
         */
        // U+001F UNIT SEPARATOR: a control character that cannot appear in clinical
        // prose, so no note content can imitate a field boundary.
        const char FieldSeparator = '';

        var canonical = new StringBuilder()
            .Append("S:").Append(Subjective).Append(FieldSeparator)
            .Append("O:").Append(Objective).Append(FieldSeparator)
            .Append("A:").Append(Assessment).Append(FieldSeparator)
            .Append("P:").Append(Plan)
            .ToString();

        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }

    private static string Trim(string? value) => value?.Trim() ?? string.Empty;
}

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1720:Identifier contains type name",
    Justification = "'Signed' is the clinical term for an attested note. The analyzer " +
                    "matches it against the 'signed' numeric type; renaming would make the " +
                    "domain vocabulary wrong to satisfy a false positive.")]
public enum NoteStatus
{
    Draft = 1,

    /// <summary>Signed and current.</summary>
    Signed = 2,

    /// <summary>Signed, then superseded by a later version. Content retained.</summary>
    Amended = 3,
}

public enum NoteOrigin
{
    Manual = 1,

    /// <summary>Drafted from a dictation, then reviewed and signed by the clinician.</summary>
    DictationAssisted = 2,
}
