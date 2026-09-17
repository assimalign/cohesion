namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes data mounted into a realized resource.
/// </summary>
public sealed record ResourceManifestMount
{
    /// <summary>
    /// Gets the mount's logical name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the kind of data carried by the mount.
    /// </summary>
    public ResourceMountKind Kind { get; init; }

    /// <summary>
    /// Gets the path at which the mount is materialized inside the resource.
    /// </summary>
    public string ContainerPath { get; init; } = string.Empty;

    /// <summary>
    /// Gets the requested capacity for a <see cref="ResourceMountKind.Volume"/> mount.
    /// </summary>
    public string? Size { get; init; }

    /// <summary>
    /// Gets the parameter, resource key, or literal source from which the mount is populated.
    /// </summary>
    public string? Source { get; init; }
}
