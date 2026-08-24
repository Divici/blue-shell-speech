using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Practice.Domain.Auditing;

namespace Practice.Infrastructure.Persistence.Configurations;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("AuditEvents");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();
        builder.HasIndex(e => e.PublicId).IsUnique();

        builder.Property(e => e.OccurredAtUtc).IsRequired();
        builder.Property(e => e.EventType).IsRequired();
        builder.Property(e => e.Outcome).IsRequired();

        builder.Property(e => e.ActorUserId).HasMaxLength(450);
        builder.Property(e => e.EntityType).HasMaxLength(100);
        builder.Property(e => e.CorrelationId).HasMaxLength(100);

        // Long enough for IPv6 with a zone index; short enough to reject nonsense.
        builder.Property(e => e.IpAddress).HasMaxLength(64);
        builder.Property(e => e.UserAgent).HasMaxLength(512);
        builder.Property(e => e.Metadata).HasMaxLength(4000);

        /*
         * The queries this table is actually read by during an investigation:
         *   "what happened around this time"           -> OccurredAtUtc
         *   "who touched this record"                  -> EntityType + EntityPublicId
         *   "how many failed logins from this actor"   -> EventType + OccurredAtUtc
         */
        builder.HasIndex(e => e.OccurredAtUtc);
        builder.HasIndex(e => new { e.EntityType, e.EntityPublicId });
        builder.HasIndex(e => new { e.EventType, e.OccurredAtUtc });

        /*
         * No RowVersion, and no concurrency token.
         *
         * Audit rows are inserted and never updated — the application principal has no
         * UPDATE or DELETE grant on this table. A concurrency token would imply an update
         * path that must not exist.
         */
        builder.Ignore(e => e.RowVersion);
    }
}
