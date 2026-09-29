namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Provides the common platform-neutral overrides available to every planned resource.
/// Resource-area option types may derive from this class to add kind-specific facts.
/// </summary>
public class ResourceOptions : IResourceOptions
{
    /// <inheritdoc />
    public int? Replicas { get; set; }

    /// <inheritdoc />
    public ResourceStorageOptions Storage { get; } = new();
}
