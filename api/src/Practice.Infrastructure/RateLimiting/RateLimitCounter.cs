using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Practice.Infrastructure.RateLimiting;

/// <summary>
/// One partition's count, and when its window ends.
///
/// THE SHARED STORE, AND THE WHOLE REASON THIS IS A TABLE RATHER THAN A DICTIONARY.
/// <c>api</c> is a Container App that scales horizontally and to zero. An in-process counter
/// limits ONE replica — an attacker round-robins the rest and multiplies the limit by the
/// replica count — and it forgets everything the moment the last replica is scaled away,
/// which on this tier is minutes of idleness. That is the shape of control WORK_QUEUE 1.18
/// refused to ship, in its own words, on the grounds that a control that looks present and
/// is not is worse than an absent one. <c>web/lib/rate-limit.ts</c> is the in-process
/// version and says the same thing about itself.
///
/// NOT A DOMAIN ENTITY, deliberately. It does not derive from <c>Entity</c>: no
/// <c>PublicId</c> (nothing ever addresses one), no <c>RowVersion</c> (the atomicity is the
/// statement, not an optimistic check — see <see cref="SqlRateLimitStore"/>), no
/// <c>ProviderId</c> (a login attempt has no provider, and the row it counts is generated
/// by people who are not Michelle). It holds no PHI and no address: the partition key is a
/// hash, which is the same argument <c>ConsultationRequest.SourceIpHash</c> makes for
/// keeping a visitor count without keeping a record of who visited.
///
/// IT EXISTS AS A TYPE SO THE MIGRATION CAN BE GENERATED. Nothing reads or writes it through
/// the change tracker; every access is the one statement in
/// <see cref="SqlRateLimitStore"/>, because a read-modify-write through EF is precisely the
/// race D097 measured on <c>AccessFailedCount</c>.
/// </summary>
public sealed class RateLimitCounter
{
    /// <summary>
    /// <c>policy|kind|hash</c> — for example <c>login-account|account|9f86d081…</c>.
    ///
    /// The policy name is IN the key rather than beside it, so two policies over the same
    /// hash never share a bucket and a single-column primary key is a single seek. The hash
    /// is 64 hex characters (SHA-256), which is what
    /// <see cref="Practice.Infrastructure.RateLimiting.RateLimitPartition"/> guarantees.
    /// </summary>
    public string PartitionKey { get; set; } = string.Empty;

    /// <summary>Requests counted in the current window, capped one past the limit.</summary>
    public int Count { get; set; }

    /// <summary>
    /// When the current window expires — stored as an END rather than a start so the
    /// comparison the statement makes on every request is a bare column against a
    /// parameter, and so the sweep can seek on the same index.
    /// </summary>
    public DateTime WindowEndsAtUtc { get; set; }
}

internal sealed class RateLimitCounterConfiguration : IEntityTypeConfiguration<RateLimitCounter>
{
    public void Configure(EntityTypeBuilder<RateLimitCounter> builder)
    {
        builder.ToTable("RateLimitCounters");

        /*
         * The partition key IS the key, clustered.
         *
         * Every access is an exact-match seek on it, and the table is small by construction
         * (the sweep in SqlRateLimitStore keeps it drained). A surrogate identity key plus a
         * unique index would add a level of indirection to the one operation this table
         * performs, and the width that saves is width on a table with no other index.
         *
         * 200 characters holds the longest key any policy can produce with room to spare —
         * the longest policy name here is 16 and the hash is 64 — and bounds what a caller
         * inside the environment can make this column hold.
         */
        builder.HasKey(c => c.PartitionKey);
        builder.Property(c => c.PartitionKey).HasMaxLength(200).IsUnicode(false);

        builder.Property(c => c.Count).IsRequired();
        builder.Property(c => c.WindowEndsAtUtc).HasColumnType("datetime2(3)").IsRequired();

        /*
         * The sweep's index.
         *
         * Without it the opportunistic DELETE in SqlRateLimitStore is a scan of the whole
         * table on every request, which is the wrong shape for a statement that runs on the
         * hot path of the endpoint an attacker is flooding.
         */
        builder.HasIndex(c => c.WindowEndsAtUtc);
    }
}

/// <summary>
/// What one statement decided, read back as a row.
///
/// A KEYLESS TYPE RATHER THAN A SCALAR because three facts come out of the same statement
/// and re-reading the row to get the other two would race with the next request. It is
/// mapped to no table and no view, so migrations ignore it entirely; the only way it is ever
/// materialised is <see cref="SqlRateLimitStore"/>'s <c>FromSqlInterpolated</c>.
/// </summary>
public sealed class RateLimitOutcome
{
    /// <summary>
    /// The count already standing in the CURRENT window before this request — zero when
    /// this request is the one that opened the window.
    ///
    /// It exists for one purpose: telling the request that CROSSED the limit apart from the
    /// ones behind it, so the audit row is written once per window rather than once per
    /// refused request. See <see cref="SqlRateLimitStore"/>.
    /// </summary>
    public int Before { get; set; }

    /// <summary>The count after this request, capped at one past the limit.</summary>
    public int After { get; set; }

    /// <summary>When the window this request landed in expires.</summary>
    public DateTime ResetsAtUtc { get; set; }
}

internal sealed class RateLimitOutcomeConfiguration : IEntityTypeConfiguration<RateLimitOutcome>
{
    public void Configure(EntityTypeBuilder<RateLimitOutcome> builder) =>
        builder.HasNoKey().ToView(null);
}
