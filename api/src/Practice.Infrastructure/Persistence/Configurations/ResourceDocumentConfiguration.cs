using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Practice.Domain.Resources;

namespace Practice.Infrastructure.Persistence.Configurations;

public sealed class ResourceDocumentConfiguration : IEntityTypeConfiguration<ResourceDocument>
{
    public void Configure(EntityTypeBuilder<ResourceDocument> builder)
    {
        builder.ToTable("ResourceDocuments");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedOnAdd();
        builder.HasIndex(r => r.PublicId).IsUnique();

        builder.Property(r => r.ProviderId).IsRequired();

        /*
         * The column widths are the numbers the aggregate enforces, for the reason
         * ConsultationRequests spells out: a column with more room than the aggregate
         * allows is a second, quieter limit that only a raw INSERT can reach.
         */
        builder.Property(r => r.Title)
            .HasMaxLength(ResourceDocument.MaxTitleLength).IsRequired();
        builder.Property(r => r.Description)
            .HasMaxLength(ResourceDocument.MaxDescriptionLength);
        builder.Property(r => r.BlobUri)
            .HasMaxLength(ResourceDocument.MaxBlobUriLength).IsRequired();

        /*
         * varchar for the slug and the content type: both are ASCII by definition. A URL
         * segment is lower-case letters, digits and hyphens; a media type is a registered
         * ASCII token. A Unicode column would accept a slug that renders identically to
         * another one and resolves to a different page.
         */
        builder.Property(r => r.Slug)
            .HasColumnType($"varchar({ResourceDocument.MaxSlugLength})").IsRequired();
        builder.Property(r => r.ContentType)
            .HasColumnType($"varchar({ResourceDocument.MaxContentTypeLength})").IsRequired();

        builder.Property(r => r.FileSizeBytes).IsRequired();
        builder.Property(r => r.RevisionNumber).IsRequired();
        builder.Property(r => r.IsPublished).IsRequired();
        builder.Property(r => r.SortOrder).IsRequired();
        builder.Property(r => r.RowVersion).IsRowVersion();

        /*
         * UNIQUE ACROSS THE WHOLE TABLE, not per provider.
         *
         * /resources/{slug} has no tenant segment in it, so two clinicians cannot both own
         * /resources/tummy-time — the URL would be ambiguous and one of them would win by
         * insertion order. Scoping this index to ProviderId would look more consistent with
         * every other index here and would be wrong, because the constraint comes from the
         * public route rather than from tenancy.
         */
        builder.HasIndex(r => r.Slug)
            .IsUnique()
            .HasDatabaseName("UX_ResourceDocuments_OneDocumentPerPublicUrl");

        // The index page: this provider's published handouts, in the order they set.
        builder.HasIndex(r => new { r.ProviderId, r.IsPublished, r.SortOrder });

        /*
         * A published handout knows when it went up.
         *
         * The aggregate sets the two together and never clears the date. The CHECK is what
         * makes it true of a row nothing in the application wrote — and this is the one
         * table meant to be read by the public, so "is it live" is a question answered on
         * an anonymous request where nothing else is in the way.
         */
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_ResourceDocuments_PublishedRowsCarryADate",
            "([IsPublished] = 0) OR ([PublishedAtUtc] IS NOT NULL)"));

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_ResourceDocuments_FileHasBytes",
            $"[FileSizeBytes] > 0 AND [FileSizeBytes] <= {ResourceDocument.MaxFileSizeBytes}"));

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_ResourceDocuments_RevisionsStartAtOne",
            "[RevisionNumber] >= 1"));
    }
}
