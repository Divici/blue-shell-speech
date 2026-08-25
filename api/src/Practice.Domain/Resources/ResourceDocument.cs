using System.Text.RegularExpressions;
using Practice.Domain.Common;

namespace Practice.Domain.Resources;

/// <summary>
/// A downloadable parent handout (presearch §4.1).
///
/// SHIPS EMPTY, like Encounter, and for a related reason: CLAUDE.md removes the Resources
/// tab until handouts exist but requires that "adding one later is a content change". That
/// sentence is a constraint on this class. Everything the public page will need — the URL
/// segment, the ordering, whether it is up, when it went up — has to be a column now, or
/// the first handout is a migration.
///
/// **NOT PHI, and not a patient file.** This is practice-authored content meant to be read
/// by anyone. Patient document upload is a separate entity when it arrives
/// (docs/DATA_MODEL.md): one table holding both would put a public blob container and a
/// clinical one behind the same rows, one forgotten predicate away from serving a child's
/// evaluation report from a marketing page.
///
/// **It still carries a ProviderId**, per CLAUDE.md's convention, and it is still filtered
/// like every other tenant table. The public read path is a deliberate, greppable
/// IgnoreQueryFilters() scoped to published rows when it is built — one visible opt-out
/// beats a query filter with a special case in it, because the special case would be
/// inherited by whatever gets added to this table next.
/// </summary>
public sealed partial class ResourceDocument : Entity
{
    // EF Core materialisation only.
    private ResourceDocument() { }

    /// <summary>
    /// Who authored it. Not a patient scope — a handout belongs to the practice, not to a
    /// child — and not absent, because a tenancy discriminator retrofitted onto a live
    /// table is the migration this whole convention exists to avoid.
    /// </summary>
    public long ProviderId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    /// <summary>
    /// The public URL segment: /resources/{slug}. Unique across the whole table.
    ///
    /// Across the WHOLE table, not per provider, because the public route has no tenant in
    /// it. Two clinicians cannot both own /resources/tummy-time; the second one has to pick
    /// a different name, and the database is what tells them so.
    /// </summary>
    public string Slug { get; private set; } = string.Empty;

    /// <summary>One or two sentences on the index page. Optional.</summary>
    public string? Description { get; private set; }

    /// <summary>Blob Storage reference. The file itself never lives in SQL.</summary>
    public string BlobUri { get; private set; } = string.Empty;

    /// <summary>
    /// What the browser will do with the bytes — an allowlist, not a label.
    ///
    /// This is the only table in the system meant to be served to anonymous readers, so an
    /// text/html or image/svg+xml handout is script execution on the practice's own origin.
    /// </summary>
    public string ContentType { get; private set; } = string.Empty;

    public long FileSizeBytes { get; private set; }

    /// <summary>
    /// Bumped when the FILE is replaced, starting at 1.
    ///
    /// A handout is content, not a clinical record: nobody signs it and nothing attests to
    /// it, so the superseding-row chain ClinicalNote needs would buy nothing. What a reader
    /// does need is to know the PDF they downloaded in March is not the one on the page
    /// now, and that is one integer.
    /// </summary>
    public int RevisionNumber { get; private set; } = 1;

    /// <summary>When the file was last replaced. Null while it is still the original.</summary>
    public DateTime? ContentUpdatedAtUtc { get; private set; }

    public bool IsPublished { get; private set; }

    /// <summary>
    /// When it FIRST went up. Never cleared, not even by a withdrawal.
    ///
    /// "How long has this been in front of families" is a question about the whole history,
    /// and it is the question that matters if a handout ever has to be corrected for a
    /// safety reason. A field reset on every republish would answer something else while
    /// looking like this.
    /// </summary>
    public DateTime? PublishedAtUtc { get; private set; }

    /// <summary>
    /// When it most recently came down. Null while it is up, and null on a draft that was
    /// never published — the two are distinguished by PublishedAtUtc.
    /// </summary>
    public DateTime? WithdrawnAtUtc { get; private set; }

    /// <summary>
    /// Where it sits on the index page. Ordering is content, so reordering the handouts is
    /// not a deployment.
    /// </summary>
    public short SortOrder { get; private set; }

    public const int MaxTitleLength = 160;
    public const int MaxSlugLength = 80;
    public const int MaxDescriptionLength = 500;
    public const int MaxBlobUriLength = 500;
    public const int MaxContentTypeLength = 100;

    /// <summary>
    /// 20 MB. A parent handout larger than this is a mistake, and the family downloading it
    /// is frequently on a phone in a car park.
    /// </summary>
    public const long MaxFileSizeBytes = 20L * 1024 * 1024;

    /// <summary>
    /// PDF and two raster image formats. Nothing a browser will execute, and nothing whose
    /// parser is a known liability.
    ///
    /// Widening this is a code change with a test attached, which is the point. SVG is
    /// absent deliberately: it is a document format that runs script.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedContentTypes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "application/pdf",
            "image/png",
            "image/jpeg",
        };

    public static ResourceDocument Draft(
        long providerId,
        string title,
        string slug,
        string? description,
        string blobUri,
        string contentType,
        long fileSizeBytes,
        short sortOrder = 0)
    {
        if (providerId <= 0)
        {
            throw new ArgumentException(
                "A resource needs a provider.", nameof(providerId));
        }

        return new ResourceDocument
        {
            ProviderId = providerId,
            Title = Guard.MaxLength(Guard.NotBlank(title, "title"), MaxTitleLength, "title"),
            Slug = ValidatedSlug(slug),
            Description = string.IsNullOrWhiteSpace(description)
                ? null
                : Guard.MaxLength(description.Trim(), MaxDescriptionLength, "description"),
            BlobUri = ValidatedBlobUri(blobUri),
            ContentType = ValidatedContentType(contentType),
            FileSizeBytes = ValidatedFileSize(fileSizeBytes),
            SortOrder = sortOrder,
        };
    }

    /// <summary>
    /// Puts it in front of families. Idempotent, and safe after a withdrawal.
    /// </summary>
    public void Publish(DateTime publishedAtUtc)
    {
        if (publishedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "A publication time must be UTC. Convert before calling.",
                nameof(publishedAtUtc));
        }

        IsPublished = true;
        PublishedAtUtc ??= publishedAtUtc;
        WithdrawnAtUtc = null;
    }

    /// <summary>
    /// Takes it down. The ROW survives.
    ///
    /// There is no delete here and no retention clock. A handout that families were sent to
    /// is part of the record of what the practice told them, and a withdrawn row plus its
    /// blob is what answers "what did that page say in March" — which is exactly the
    /// question asked when a handout has to be corrected for a clinical reason. Nothing in
    /// this table is PHI, so nothing obliges its deletion.
    /// </summary>
    public void Withdraw(DateTime withdrawnAtUtc)
    {
        if (withdrawnAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "A withdrawal time must be UTC. Convert before calling.",
                nameof(withdrawnAtUtc));
        }

        if (!IsPublished)
        {
            throw new InvalidOperationException(
                "This handout is not published. There is nothing to take down.");
        }

        IsPublished = false;
        WithdrawnAtUtc = withdrawnAtUtc;
    }

    /// <summary>
    /// Corrects the file behind an existing URL, keeping the slug and the publication
    /// history. See RevisionNumber.
    /// </summary>
    public void ReplaceFile(
        string blobUri, string contentType, long fileSizeBytes, DateTime replacedAtUtc)
    {
        if (replacedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "A revision time must be UTC. Convert before calling.", nameof(replacedAtUtc));
        }

        BlobUri = ValidatedBlobUri(blobUri);
        ContentType = ValidatedContentType(contentType);
        FileSizeBytes = ValidatedFileSize(fileSizeBytes);
        RevisionNumber++;
        ContentUpdatedAtUtc = replacedAtUtc;
    }

    /// <summary>
    /// Editorial changes. The slug is NOT among them: it is a published URL, and a page
    /// that silently moves is a link a parent has that stops working.
    /// </summary>
    public void UpdateDetails(string title, string? description, short sortOrder)
    {
        Title = Guard.MaxLength(Guard.NotBlank(title, "title"), MaxTitleLength, "title");
        Description = string.IsNullOrWhiteSpace(description)
            ? null
            : Guard.MaxLength(description.Trim(), MaxDescriptionLength, "description");
        SortOrder = sortOrder;
    }

    /// <summary>
    /// Lower case, digits and single hyphens. Refused rather than normalised.
    ///
    /// Normalising means two different titles can silently collapse onto one URL, and the
    /// second publish then fails against a unique index — an error about an index, raised
    /// at the wrong moment, about a name somebody chose deliberately.
    /// </summary>
    private static string ValidatedSlug(string slug)
    {
        var trimmed = (slug ?? string.Empty).Trim();

        if (trimmed.Length > MaxSlugLength || !UrlSegment().IsMatch(trimmed))
        {
            throw new ArgumentException(
                "A resource address is lower-case words separated by single hyphens, "
                + $"{MaxSlugLength} characters or fewer.",
                nameof(slug));
        }

        return trimmed;
    }

    private static string ValidatedContentType(string contentType)
    {
        var trimmed = (contentType ?? string.Empty).Trim();

        if (!AllowedContentTypes.Contains(trimmed))
        {
            throw new ArgumentException(
                $"A handout is one of: {string.Join(", ", AllowedContentTypes)}.",
                nameof(contentType));
        }

        return trimmed;
    }

    /// <summary>
    /// Absolute https, nothing else.
    ///
    /// A relative path would be resolved against whatever host reads the row, and a
    /// file:// or http:// reference is a mixed-content download on a page parents open on
    /// public wifi.
    /// </summary>
    private static string ValidatedBlobUri(string blobUri)
    {
        var trimmed = (blobUri ?? string.Empty).Trim();

        if (trimmed.Length > MaxBlobUriLength
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                "A resource file location must be an absolute https URL.", nameof(blobUri));
        }

        return trimmed;
    }

    private static long ValidatedFileSize(long fileSizeBytes)
    {
        if (fileSizeBytes <= 0 || fileSizeBytes > MaxFileSizeBytes)
        {
            throw new ArgumentException(
                $"A handout is between 1 byte and {MaxFileSizeBytes / (1024 * 1024)} MB.",
                nameof(fileSizeBytes));
        }

        return fileSizeBytes;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex UrlSegment();
}
