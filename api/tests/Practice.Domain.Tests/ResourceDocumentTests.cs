using Practice.Domain.Resources;

namespace Practice.Domain.Tests;

/// <summary>
/// A parent handout, tested with no database and no framework.
///
/// The Resources tab is hidden until a row exists (CLAUDE.md), and the point of shipping
/// the entity empty is that adding the first handout is then a CONTENT change rather than
/// a migration. That constrains the model more than it looks: everything the public page
/// will need — the URL segment, the ordering, whether it is up, when it went up — has to
/// be a column now.
///
/// NOT PHI. This is the one table in the system deliberately meant to be read by the
/// public, which is why the content-type allowlist below is a security control and not
/// tidiness: a text/html blob served from the practice's own domain is stored XSS.
///
/// SYNTHETIC DATA ONLY.
/// </summary>
public sealed class ResourceDocumentTests
{
    private const long Provider = 7;

    private static readonly DateTime Published =
        new(2026, 8, 25, 14, 30, 0, DateTimeKind.Utc);

    private static ResourceDocument Draft(
        string title = "Talking with your toddler at mealtimes",
        string slug = "talking-at-mealtimes",
        string blobUri = "https://storage.example.invalid/resources/mealtimes.pdf",
        string contentType = "application/pdf",
        long fileSizeBytes = 240_000) =>
        ResourceDocument.Draft(
            Provider, title, slug, "Ten minutes a day, no flashcards.",
            blobUri, contentType, fileSizeBytes);

    // ------------------------------------------------------------- the public URL

    /// <summary>
    /// The slug is the public URL segment (/resources/{slug}), so it is lower case,
    /// hyphen-separated, and refused rather than normalised.
    ///
    /// Refused, because normalising means two titles can silently collapse onto one URL
    /// and the second publish fails against a unique index with an error about an index
    /// rather than about a name somebody chose.
    ///
    /// Control: ValidatedSlug — the length and UrlSegment() regex check.
    /// Deleted → red on every case, "Assert.Throws() Failure: No exception was
    /// thrown / Expected: typeof(System.ArgumentException)".
    /// </summary>
    [Theory]
    [InlineData("Talking-At-Mealtimes")]   // upper case: two URLs for one document
    [InlineData("talking at mealtimes")]   // spaces
    [InlineData("talking--at-mealtimes")]  // empty segment
    [InlineData("-mealtimes")]
    [InlineData("mealtimes-")]
    [InlineData("mealtimes/pdf")]
    public void A_slug_that_is_not_a_url_segment_is_refused(string slug)
    {
        var error = Assert.Throws<ArgumentException>(() => Draft(slug: slug));

        Assert.Equal("slug", error.ParamName);
    }

    // ------------------------------------------------------------- the file

    /// <summary>
    /// An allowlist, not a blocklist.
    ///
    /// This table is the only one meant to be served to anonymous readers, so the content
    /// type decides what a browser does with the bytes. text/html or image/svg+xml from
    /// the practice's own origin is script execution on the practice's own domain.
    ///
    /// Control: ValidatedContentType — the AllowedContentTypes membership check.
    /// Deleted → red on every case, "Assert.Throws() Failure: No exception was
    /// thrown / Expected: typeof(System.ArgumentException)".
    /// </summary>
    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("application/octet-stream")]
    public void A_content_type_outside_the_allowlist_is_refused(string contentType)
    {
        var error = Assert.Throws<ArgumentException>(() => Draft(contentType: contentType));

        Assert.Equal("contentType", error.ParamName);
    }

    /// <summary>
    /// Control: ValidatedBlobUri — the absolute-https check.
    /// Deleted → red on every case, "Assert.Throws() Failure: No exception was
    /// thrown / Expected: typeof(System.ArgumentException)".
    /// </summary>
    [Theory]
    [InlineData("/resources/mealtimes.pdf")]
    [InlineData("http://storage.example.invalid/resources/mealtimes.pdf")]
    [InlineData("file:///c:/handouts/mealtimes.pdf")]
    public void A_file_location_that_is_not_absolute_https_is_refused(string blobUri)
    {
        var error = Assert.Throws<ArgumentException>(() => Draft(blobUri: blobUri));

        Assert.Equal("blobUri", error.ParamName);
    }

    /// <summary>
    /// Control: ValidatedFileSize — the `fileSizeBytes > MaxFileSizeBytes` clause.
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown / Expected:
    /// typeof(System.ArgumentException)".
    /// </summary>
    [Fact]
    public void A_handout_larger_than_the_cap_is_refused()
    {
        var error = Assert.Throws<ArgumentException>(
            () => Draft(fileSizeBytes: ResourceDocument.MaxFileSizeBytes + 1));

        Assert.Equal("fileSizeBytes", error.ParamName);
    }

    /// <summary>
    /// Control: ValidatedFileSize — the `fileSizeBytes &lt;= 0` clause. The two clauses are
    /// deleted separately: one predicate, and either half would answer for the other on a
    /// value outside both bounds (D077).
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown / Expected:
    /// typeof(System.ArgumentException)".
    /// </summary>
    [Fact]
    public void A_handout_with_no_bytes_in_it_is_refused()
    {
        var error = Assert.Throws<ArgumentException>(() => Draft(fileSizeBytes: 0));

        Assert.Equal("fileSizeBytes", error.ParamName);
    }

    // ------------------------------------------------------------- publication

    /// <summary>
    /// A new handout is not live. Publishing is an act somebody takes.
    ///
    /// The three publication fields have no initialiser to delete — false and null are the
    /// language's defaults, and that they are the SAFE defaults is the point: a row that
    /// appears on the public site the moment it is inserted is a draft published by
    /// accident. The revision is the one value somebody had to choose.
    ///
    /// Control: the `= 1` initialiser on ResourceDocument.RevisionNumber.
    /// Deleted → red, "Assert.Equal() Failure: Values differ / Expected: 1 / Actual: 0".
    /// </summary>
    [Fact]
    public void A_new_handout_is_a_draft_that_has_never_been_published()
    {
        var document = Draft();

        Assert.False(document.IsPublished);
        Assert.Null(document.PublishedAtUtc);
        Assert.Null(document.WithdrawnAtUtc);
        Assert.Equal(1, document.RevisionNumber);
    }

    /// <summary>
    /// Control: ResourceDocument.Withdraw — the WithdrawnAtUtc assignment.
    /// Deleted → red, "Assert.Equal() Failure: Values differ / Expected:
    /// 2026-09-24T14:30:00.0000000Z / Actual: null".
    /// </summary>
    [Fact]
    public void Withdrawing_a_handout_records_when_it_came_down()
    {
        var document = Draft();
        document.Publish(Published);

        document.Withdraw(Published.AddDays(30));

        Assert.False(document.IsPublished);
        Assert.Equal(Published.AddDays(30), document.WithdrawnAtUtc);
    }

    /// <summary>
    /// PublishedAtUtc is the date it FIRST went up, and nothing clears it.
    ///
    /// "How long has this handout been in front of families" is a question about the whole
    /// history, and it is the question that matters if a handout ever has to be corrected
    /// for a safety reason. A field that reset on every republish would answer a different
    /// question while looking like this one.
    ///
    /// Control: ResourceDocument.Publish — the `PublishedAtUtc ??=` null-coalescing
    /// assignment. Replaced with a plain `=` → red, "Assert.Equal() Failure: Values differ
    /// / Expected: 2026-08-25T14:30:00.0000000Z / Actual: 2026-10-24T14:30:00.0000000Z".
    /// </summary>
    [Fact]
    public void Republishing_keeps_the_date_the_handout_first_went_up()
    {
        var document = Draft();
        document.Publish(Published);
        document.Withdraw(Published.AddDays(30));

        document.Publish(Published.AddDays(60));

        Assert.Equal(Published, document.PublishedAtUtc);
    }

    /// <summary>
    /// Control: ResourceDocument.Publish — the `WithdrawnAtUtc = null` reset.
    /// Deleted → red, "Assert.Null() Failure: Value of type 'Nullable&lt;DateTime&gt;' has a
    /// value / Expected: null / Actual: 2026-09-24T14:30:00.0000000Z".
    /// </summary>
    [Fact]
    public void Republishing_clears_the_withdrawal()
    {
        var document = Draft();
        document.Publish(Published);
        document.Withdraw(Published.AddDays(30));

        document.Publish(Published.AddDays(60));

        Assert.True(document.IsPublished);
        Assert.Null(document.WithdrawnAtUtc);
    }

    /// <summary>
    /// Control: ResourceDocument.Withdraw — the `!IsPublished` guard.
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown / Expected:
    /// typeof(System.InvalidOperationException)".
    /// </summary>
    [Fact]
    public void A_handout_that_was_never_published_cannot_be_withdrawn()
    {
        var document = Draft();

        Assert.Throws<InvalidOperationException>(() => document.Withdraw(Published));
    }

    /// <summary>
    /// Control: ResourceDocument.Publish — the publishedAtUtc.Kind check.
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown / Expected:
    /// typeof(System.ArgumentException)".
    /// </summary>
    [Fact]
    public void A_publication_time_that_is_not_utc_is_refused()
    {
        var document = Draft();
        var local = new DateTime(2026, 8, 25, 10, 30, 0, DateTimeKind.Local);

        var error = Assert.Throws<ArgumentException>(() => document.Publish(local));

        Assert.Equal("publishedAtUtc", error.ParamName);
    }

    // ------------------------------------------------------------- revisions

    /// <summary>
    /// Correcting a handout replaces the file and bumps a revision. It does NOT create a
    /// second row.
    ///
    /// A handout is content, not a clinical record: nobody signs it and nothing attests to
    /// it, so the superseding-row chain that ClinicalNote needs would buy nothing here. The
    /// revision number is what tells a reader the PDF they downloaded in March is not the
    /// one on the page now.
    ///
    /// Control: ResourceDocument.ReplaceFile — the RevisionNumber increment.
    /// Deleted → red, "Assert.Equal() Failure: Values differ / Expected: 2 / Actual: 1".
    /// </summary>
    [Fact]
    public void Replacing_the_file_bumps_the_revision_and_keeps_the_url()
    {
        var document = Draft();
        document.Publish(Published);

        document.ReplaceFile(
            "https://storage.example.invalid/resources/mealtimes-v2.pdf",
            "application/pdf",
            260_000,
            Published.AddDays(45));

        Assert.Equal(2, document.RevisionNumber);
        Assert.Equal("talking-at-mealtimes", document.Slug);
        Assert.Equal(Published.AddDays(45), document.ContentUpdatedAtUtc);
    }

    /// <summary>
    /// The allowlist binds the replacement too, not only the first upload.
    ///
    /// Control: ResourceDocument.ReplaceFile — the ValidatedContentType call.
    /// Replaced with a straight assignment → red, "Assert.Throws() Failure: No exception
    /// was thrown / Expected: typeof(System.ArgumentException)".
    /// </summary>
    [Fact]
    public void A_replacement_file_is_held_to_the_same_allowlist()
    {
        var document = Draft();

        var error = Assert.Throws<ArgumentException>(() => document.ReplaceFile(
            "https://storage.example.invalid/resources/mealtimes.html",
            "text/html",
            1_000,
            Published));

        Assert.Equal("contentType", error.ParamName);
    }
}
