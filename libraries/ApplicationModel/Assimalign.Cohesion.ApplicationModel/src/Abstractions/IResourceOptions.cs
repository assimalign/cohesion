namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Exposes deployer-owned, platform-neutral overrides used while planning a resource.
/// </summary>
public interface IResourceOptions
{
    /// <summary>
    /// Gets the requested replica count, or <see langword="null"/> to use the manifest value.
    /// </summary>
    int? Replicas { get; }

    /// <summary>Gets storage overrides for the resource.</summary>
    ResourceStorageOptions Storage { get; }
}
