using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Practice.Infrastructure.RateLimiting;

/// <summary>
/// Which bucket a request counts against, and what the bucket is allowed to know.
///
/// TWO DIMENSIONS, AND BOTH ARE NEEDED FOR DIFFERENT REASONS.
///
///   SOURCE   bounds a flood from one place across every address it tries. Without it, an
///            attacker walks an address list and every guess lands in a fresh account
///            bucket, so a per-account limit alone counts to one forever.
///   ACCOUNT  bounds a flood at one address across every place it comes from. Without it, a
///            distributed caller gets the source limit MULTIPLIED by however many sources
///            it has.
///
/// Neither is a bound on the other and neither is sold as complete: a caller with a wide
/// enough set of sources AND a wide enough address list is limited by the product of the
/// two, not by either. That is the honest ceiling of any counting limiter and it is why the
/// lockout, the audit trail and MFA are all still load-bearing.
///
/// EVERYTHING IS HASHED, INCLUDING THE THINGS THAT ARE NOT SECRET. The store keeps counts,
/// not identities: an address in a table is a record of who tried to sign in, and a
/// submitted email address in a table is the beginning of the enumeration list this whole
/// task exists to deny. SHA-256 of a normalised value keeps every question the counter is
/// ever asked answerable — "is this the same caller as that one" — and answers none of the
/// questions it should not be able to. Same argument as
/// <c>ConsultationRequest.SourceIpHash</c> (D080).
/// </summary>
public static class RateLimitPartition
{
    /// <summary>Hex characters in a SHA-256 digest.</summary>
    private const int HashLength = 64;

    /// <summary>
    /// The bucket every caller whose source could not be established shares.
    ///
    /// A SHARED CONSTANT, NOT A UNIQUE VALUE, and the direction matters. A unique fallback —
    /// a new GUID, the trace id — would give every unattributed request its own bucket,
    /// which is a limiter switched off precisely where it has least to go on. A shared one
    /// OVER-throttles: everything arriving without a forwarded key competes for one
    /// allowance. That is the same trade <c>clientIdentifier</c> makes in <c>web</c>, one
    /// tier along, and for the same reason.
    ///
    /// It is deliberately not a valid hash, so it can never collide with a real one.
    /// </summary>
    public const string Unattributed = "unattributed";

    /// <summary>
    /// The key a counter row is stored under: <c>policy|kind|value</c>.
    ///
    /// The policy name is part of the key rather than a second column so that one seek
    /// answers one question, and so two policies over the same hash can never share a count.
    /// </summary>
    public static string KeyFor(RateLimitPolicy policy, string kind, string value) =>
        string.Create(
            CultureInfo.InvariantCulture, $"{policy.Name}|{kind}|{value}");

    /// <summary>
    /// The forwarded source key, accepted only in the shape this application produces.
    ///
    /// WHAT ARRIVES HERE IS ALREADY A HASH. <c>web</c> computes
    /// <c>hashClientId(clientIdentifier(x-forwarded-for))</c> — the same single derivation
    /// that keys the consultation limiter and fills
    /// <c>ConsultationRequest.SourceIpHash</c> — and forwards the result. This tier does NOT
    /// hash it again: a hash of a hash is a value that correlates with nothing either side
    /// ever recorded, and "did these attempts come from the same place as that enquiry" is
    /// the only question either column exists to answer (D080's one-hash-two-uses argument,
    /// applied across the hop).
    ///
    /// VALIDATED RATHER THAN TRUSTED, because the header is a string a caller writes.
    /// Anything that is not exactly 64 lowercase hex characters is treated as absent and
    /// falls into <see cref="Unattributed"/> — which bounds what can reach a 64-character
    /// column, stops a caller inside the environment minting a bucket per request out of
    /// arbitrary text, and keeps the failure in the over-throttling direction.
    ///
    /// AND THE TRUST IT DOES PLACE IS BOUNDED BY THE TOPOLOGY, which is worth saying where
    /// the code is rather than only in a document. Only apps inside the Container Apps
    /// environment can reach this tier's internal ingress, so today the header is as
    /// trustworthy as that network boundary and no more. Verifying that the caller really is
    /// <c>web</c> — managed identity with a validated token audience — is WORK_QUEUE 4.4,
    /// and until it lands anything that can reach <c>api</c> directly can choose its own
    /// source bucket. It cannot escape the ACCOUNT bucket by doing so, which is the second
    /// reason that dimension exists.
    /// </summary>
    public static string SourceKey(string? forwardedClientKey)
    {
        var value = forwardedClientKey?.Trim();

        if (string.IsNullOrEmpty(value) || value.Length != HashLength)
        {
            return Unattributed;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c) && c is < 'a' or > 'f')
            {
                return Unattributed;
            }
        }

        return value;
    }

    /// <summary>
    /// The bucket a submitted identity counts against — an email address, or an Identity
    /// user id on the second step.
    ///
    /// NORMALISED THE WAY IDENTITY NORMALISES, so that <c>Michelle@Example.com</c> and
    /// <c>michelle@example.com</c> are one bucket. Anything else hands an attacker a free
    /// multiplier: the same address in a different case is the same account and would
    /// otherwise be a fresh allowance.
    ///
    /// A blank or absent identity gets <see cref="Unattributed"/> rather than its own
    /// bucket, for the reason that constant exists.
    /// </summary>
    public static string AccountKey(string? submitted)
    {
        var value = submitted?.Trim();

        if (string.IsNullOrEmpty(value)) return Unattributed;

        var digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(value.ToUpperInvariant()));

        return Convert.ToHexStringLower(digest);
    }
}
