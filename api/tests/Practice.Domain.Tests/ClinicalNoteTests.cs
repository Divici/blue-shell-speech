using Practice.Domain.ClinicalNotes;

namespace Practice.Domain.Tests;

/// <summary>
/// Note immutability.
///
/// The most important rules in the system. A signed clinical note is a legal record of
/// what a clinician attested to about a child's care — if it can be silently changed,
/// nothing built on top of it can be trusted.
/// </summary>
public sealed class ClinicalNoteTests
{
    private static readonly DateTime SignedAt = new(2026, 9, 1, 20, 0, 0, DateTimeKind.Utc);

    private static ClinicalNote Draft()
    {
        var note = ClinicalNote.CreateDraft(providerId: 1, patientId: 2, appointmentId: 3);
        note.UpdateContent(
            "Mum reports Maya used 'want juice' at home.",
            "Independent requesting 60%, 80% with minimal verbal cues.",
            "Progressing toward two-word combinations.",
            "Increase requesting opportunities during play.");
        return note;
    }

    private static ClinicalNote Signed()
    {
        var note = Draft();
        note.Sign("michelle", SignedAt);
        return note;
    }

    [Fact]
    public void A_new_note_is_a_current_draft_at_version_one()
    {
        var note = Draft();

        Assert.Equal(NoteStatus.Draft, note.Status);
        Assert.Equal(1, note.VersionNumber);
        Assert.True(note.IsCurrent);
        Assert.Null(note.SupersedesNoteId);
        Assert.Null(note.ContentHash);
    }

    [Fact]
    public void A_draft_can_be_edited_freely()
    {
        var note = Draft();

        note.UpdateContent("Revised", "Revised", "Revised", "Revised");

        Assert.Equal("Revised", note.Subjective);
    }

    // ---------------------------------------------------------------- signing

    [Fact]
    public void Signing_records_who_and_when_and_fixes_the_content()
    {
        var note = Signed();

        Assert.Equal(NoteStatus.Signed, note.Status);
        Assert.Equal("michelle", note.SignedBy);
        Assert.Equal(SignedAt, note.SignedAtUtc);
        Assert.NotNull(note.ContentHash);
    }

    /// <summary>
    /// THE central rule. Editing a signed note must be impossible, not merely discouraged.
    /// </summary>
    [Fact]
    public void A_signed_note_cannot_be_edited()
    {
        var note = Signed();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            note.UpdateContent("Tampered", "Tampered", "Tampered", "Tampered"));

        Assert.Contains("amendment", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Mum reports Maya used 'want juice' at home.", note.Subjective);
    }

    [Fact]
    public void A_note_cannot_be_signed_twice()
    {
        var note = Signed();

        Assert.Throws<InvalidOperationException>(() => note.Sign("michelle", SignedAt));
    }

    /// <summary>
    /// A signature is an attestation. Attesting to nothing creates a record saying a visit
    /// was documented when it was not — worse than having no note at all.
    /// </summary>
    [Fact]
    public void An_empty_note_cannot_be_signed()
    {
        var note = ClinicalNote.CreateDraft(1, 2, 3);

        var ex = Assert.Throws<InvalidOperationException>(() => note.Sign("michelle", SignedAt));

        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_note_with_only_one_section_filled_can_be_signed()
    {
        var note = ClinicalNote.CreateDraft(1, 2, 3);
        note.UpdateContent("", "", "Session cancelled on arrival; family unwell.", "");

        note.Sign("michelle", SignedAt);

        Assert.Equal(NoteStatus.Signed, note.Status);
    }

    [Fact]
    public void Signature_time_must_be_utc()
    {
        var note = Draft();

        Assert.Throws<ArgumentException>(() =>
            note.Sign("michelle", new DateTime(2026, 9, 1, 20, 0, 0, DateTimeKind.Local)));
    }

    // ------------------------------------------------------------- amendments

    [Fact]
    public void Amending_creates_a_new_version_and_retains_the_original()
    {
        var original = Signed();
        var originalText = original.Subjective;

        var amendment = original.Amend("Corrected the accuracy figure.");

        Assert.Equal(2, amendment.VersionNumber);
        Assert.Equal(NoteStatus.Draft, amendment.Status);
        Assert.True(amendment.IsCurrent);
        Assert.Equal("Corrected the accuracy figure.", amendment.AmendmentReason);

        // The original keeps everything.
        Assert.Equal(NoteStatus.Amended, original.Status);
        Assert.False(original.IsCurrent);
        Assert.Equal(originalText, original.Subjective);
        Assert.NotNull(original.ContentHash);
        Assert.Equal("michelle", original.SignedBy);
    }

    /// <summary>The amendment starts as a copy so a clinician corrects rather than retypes.</summary>
    [Fact]
    public void An_amendment_starts_as_a_copy_of_the_previous_version()
    {
        var original = Signed();

        var amendment = original.Amend("Typo in the plan.");

        Assert.Equal(original.Subjective, amendment.Subjective);
        Assert.Equal(original.Plan, amendment.Plan);
    }

    [Fact]
    public void An_amendment_requires_a_reason()
    {
        var note = Signed();

        Assert.Throws<ArgumentException>(() => note.Amend("  "));
    }

    [Fact]
    public void A_draft_cannot_be_amended()
    {
        var note = Draft();

        var ex = Assert.Throws<InvalidOperationException>(() => note.Amend("reason"));

        Assert.Contains("edit it", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Amending a superseded version would fork the history, producing two "current"
    /// notes for one visit and no way to say which one the clinician stands behind.
    /// </summary>
    [Fact]
    public void A_superseded_version_cannot_be_amended_again()
    {
        var original = Signed();
        original.Amend("First correction.");

        var ex = Assert.Throws<InvalidOperationException>(() => original.Amend("Second."));

        Assert.Contains("superseded", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Versions_increment_across_a_chain_of_amendments()
    {
        var v1 = Signed();
        var v2 = v1.Amend("First.");
        v2.Sign("michelle", SignedAt.AddDays(1));
        var v3 = v2.Amend("Second.");

        Assert.Equal(3, v3.VersionNumber);
        Assert.False(v1.IsCurrent);
        Assert.False(v2.IsCurrent);
        Assert.True(v3.IsCurrent);
    }

    // --------------------------------------------------------------- integrity

    [Fact]
    public void A_signed_note_verifies_against_its_hash()
    {
        Assert.True(Signed().VerifyIntegrity());
    }

    /// <summary>
    /// Two notes with the same text in different sections must NOT hash the same.
    ///
    /// Without field separators in the hash input, moving a sentence from the end of
    /// Subjective to the start of Objective would produce an identical hash — a change of
    /// clinical meaning that looked untouched.
    /// </summary>
    [Fact]
    public void Moving_text_between_sections_changes_the_hash()
    {
        var a = ClinicalNote.CreateDraft(1, 2, 3);
        a.UpdateContent("alpha", "beta", "", "");
        a.Sign("michelle", SignedAt);

        var b = ClinicalNote.CreateDraft(1, 2, 3);
        b.UpdateContent("alphabeta", "", "", "");
        b.Sign("michelle", SignedAt);

        Assert.NotEqual(a.ContentHash, b.ContentHash);
    }

    [Fact]
    public void Identical_content_hashes_identically()
    {
        var a = Signed();
        var b = Signed();

        Assert.Equal(a.ContentHash, b.ContentHash);
    }

    [Fact]
    public void An_unsigned_draft_reports_integrity_trivially()
    {
        Assert.True(Draft().VerifyIntegrity());
    }

    // --------------------------------------------------------- discarding

    /*
     * Non-negotiable #5 covers SIGNED notes. An empty draft attests to nothing and
     * documents nothing, and keeping it forever leaves a permanent "Draft" badge on a
     * visit that was never documented — clearable only by writing content onto that
     * child's chart and signing it into immutability.
     */

    [Fact]
    public void An_empty_draft_can_be_discarded()
    {
        var note = ClinicalNote.CreateDraft(providerId: 1, patientId: 2, appointmentId: 3);

        Assert.True(note.CanBeDiscarded);
    }

    /// <summary>Whitespace is not content, and neither is a stray newline from a paste.</summary>
    [Fact]
    public void A_draft_holding_only_whitespace_can_be_discarded()
    {
        var note = ClinicalNote.CreateDraft(1, 2, 3);
        note.UpdateContent("   ", "\n", "\t", " ");

        Assert.True(note.CanBeDiscarded);
    }

    /// <summary>
    /// One sentence in any section is clinical content. Each is checked separately: a
    /// predicate that only looked at Subjective would discard an objective section
    /// somebody typed first.
    /// </summary>
    [Fact]
    public void A_draft_with_content_in_any_section_cannot_be_discarded()
    {
        foreach (var section in Enumerable.Range(0, 4))
        {
            var note = ClinicalNote.CreateDraft(1, 2, 3);
            var fields = new string[4];
            fields[section] = "Independent requesting 60%.";
            note.UpdateContent(fields[0], fields[1], fields[2], fields[3]);

            Assert.False(note.CanBeDiscarded);
        }
    }

    [Fact]
    public void A_signed_note_cannot_be_discarded()
    {
        Assert.False(Signed().CanBeDiscarded);
    }

    /// <summary>
    /// An amendment is a correction to a signed record, and never discardable — INCLUDING
    /// after every section has been cleared.
    ///
    /// The earlier version of this test asserted only on a freshly created amendment,
    /// whose content is still the copy Amend() made, so it passed on the emptiness clause
    /// alone and could not see whether SupersedesNoteId was examined at all. Clearing the
    /// sections first is what makes the claim in the name the claim being tested — the
    /// D066 defect, in the commit that established D066.
    ///
    /// Its docstring was false as well: it argued no amendment can ever be empty, because
    /// Sign() refuses an empty note. Sign() has nothing to do with it. An amendment is a
    /// Draft, UpdateContent edits drafts freely, and blanking one is a supported call.
    ///
    /// Control: ClinicalNote.CanBeDiscarded — the SupersedesNoteId clause.
    /// Deleted → red on the final assertion, "Assert.False() Failure — Expected: False,
    /// Actual: True".
    /// </summary>
    [Fact]
    public void An_amendment_is_never_discardable()
    {
        var amendment = Signed().Amend("Corrected the accuracy figure.");

        Assert.Equal(NoteStatus.Draft, amendment.Status);
        Assert.NotNull(amendment.AmendmentReason);
        Assert.NotNull(amendment.SupersedesNoteId);
        Assert.False(amendment.CanBeDiscarded);

        /*
         * The state the discard path can actually reach.
         *
         * PUT /notes/{amendment} with four empty strings is an ordinary edit of an
         * ordinary draft — nothing refuses it, and nothing should. What must not follow is
         * a DELETE, because the version this one supersedes is already Amended and
         * IsCurrent = 0: removing the amendment leaves the visit with no current note and
         * a signed record nothing links to.
         */
        amendment.UpdateContent("", "", "", "");

        Assert.False(amendment.CanBeDiscarded);
    }

    /// <summary>A superseded version is a retained record, not a discardable draft.</summary>
    [Fact]
    public void A_superseded_version_cannot_be_discarded()
    {
        var original = Signed();
        original.Amend("Corrected the accuracy figure.");

        Assert.Equal(NoteStatus.Amended, original.Status);
        Assert.False(original.CanBeDiscarded);
    }
}
