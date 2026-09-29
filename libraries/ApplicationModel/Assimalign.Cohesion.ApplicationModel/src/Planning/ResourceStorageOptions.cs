namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Configures deployer-owned storage facts used by a resource planner.
/// </summary>
public sealed class ResourceStorageOptions
{
    /// <summary>
    /// Gets or sets the requested size for each persistent claim, or
    /// <see langword="null"/> to use the size declared by the manifest mount.
    /// </summary>
    public string? Size { get; set; }
}
