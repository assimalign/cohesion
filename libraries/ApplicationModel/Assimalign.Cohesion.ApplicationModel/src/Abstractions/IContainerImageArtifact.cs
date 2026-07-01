namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A deployable artifact that is a pre-built container image, pinned by digest — the form
/// produced by container gateways. The digest is content-addressed and build-time immutable,
/// so pulls never resolve to stale layers; the tag is for human readability only and is
/// never used as the pull reference.
/// </summary>
public interface IContainerImageArtifact : IResourceArtifact
{
    /// <summary>
    /// The image repository, for example <c>web-administration</c>.
    /// </summary>
    string Repository { get; }

    /// <summary>
    /// The content-addressed image digest, for example <c>sha256:…</c>.
    /// </summary>
    string Digest { get; }

    /// <summary>
    /// The human-readable image tag, if any. Never used as the pull reference.
    /// </summary>
    string? Tag { get; }
}
