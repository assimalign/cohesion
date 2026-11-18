using System.Collections.Generic;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// What an environment allows of the escape hatches.
/// </summary>
/// <remarks>
/// The defaults are the v2 design's proposed defaults (its decision 14, not yet confirmed):
/// emulation denied, patches and raw manifests allowed, no raw kinds allow-listed. The record
/// takes init properties so the policy can grow without breaking callers.
/// </remarks>
public sealed record FabricEnvironmentPolicy
{
    /// <summary>
    /// Gets a value indicating whether a portable field may be emulated where the platform does not implement it natively.
    /// </summary>
    public bool AllowEmulation { get; init; }

    /// <summary>
    /// Gets a value indicating whether additive patches on platform objects the driver generates (tier T3b) are allowed.
    /// </summary>
    public bool AllowPatches { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether raw platform objects (tier T3a) are allowed.
    /// </summary>
    public bool AllowRawManifests { get; init; } = true;

    /// <summary>
    /// Gets the raw platform kinds the environment allows, as patterns such as <c>cert-manager.io/*</c>.
    /// </summary>
    public IReadOnlyList<string> RawKinds { get; init; } = [];
}
