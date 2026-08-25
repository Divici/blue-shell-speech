namespace Practice.Infrastructure.Storage;

/// <summary>
/// The blob containers this practice runs on, named once.
///
/// Created by <c>infra/provision-platform.sh</c>, which is the authority — these constants
/// exist so that application code referring to a container refers to the same string the
/// provisioning script created, rather than to a literal typed somewhere.
///
/// They are deliberately SEPARATE containers rather than prefixes in one, because they
/// have different access rules: session audio is ePHI and is reachable only by the API's
/// managed identity, and resource documents are handouts served to anonymous readers
/// (docs/THREAT_MODEL.md boundary 4, D085). One container with two prefixes would put both
/// behind one access decision.
///
/// Neither name is a secret — both are in the provisioning script in this public
/// repository — but neither is written into a health response or a log line either, and
/// that is a separate rule with a separate reason: <c>/health/ready</c> is unauthenticated,
/// so what it says is said to whoever asks.
/// </summary>
public static class StorageContainers
{
    /// <summary>Dictation audio. ePHI, retained until the note is signed (CLAUDE.md).</summary>
    public const string SessionAudio = "session-audio";

    /// <summary>Published handouts. Not PHI; served to anonymous readers (D085).</summary>
    public const string PublicResources = "public-resources";
}
