using Practice.Application.Providers;

namespace Practice.Api.Auth;

/// <summary>
/// Resolves the current provider from the request.
///
/// The BFF forwards the authenticated provider's public id on a header, having read it
/// from its own encrypted session cookie. The API resolves that to an internal id once
/// per request.
///
/// KNOWN GAP, tracked for slice 9: the header is trusted because the API has no public
/// ingress — only apps inside the Container Apps environment can reach it
/// (docs/ARCHITECTURE.md). docs/THREAT_MODEL.md boundary 2 specifies managed identity
/// with a validated token audience, which is stronger and not yet implemented. Until it
/// is, network isolation is the whole control, and that is written down rather than
/// assumed.
/// </summary>
public sealed class RequestProviderContext : IProviderContext
{
    public const string HeaderName = "X-Provider-Id";

    public long? ProviderId { get; private set; }

    /// <summary>Set once per request by the middleware below.</summary>
    internal void Resolve(long? providerId) => ProviderId = providerId;
}
