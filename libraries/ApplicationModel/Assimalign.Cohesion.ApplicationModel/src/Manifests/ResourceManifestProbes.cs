namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Groups the role-specific probes declared by a resource.
/// </summary>
public sealed record ResourceManifestProbes
{
    /// <summary>
    /// Gets the probe that determines whether dependents may start.
    /// </summary>
    public ResourceManifestProbe? Readiness { get; init; }

    /// <summary>
    /// Gets the probe that determines whether a running resource remains healthy.
    /// </summary>
    public ResourceManifestProbe? Liveness { get; init; }

    /// <summary>
    /// Gets the probe that determines whether initial startup has completed.
    /// </summary>
    public ResourceManifestProbe? Startup { get; init; }
}
