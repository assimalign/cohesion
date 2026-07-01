namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The deployable form of a resource. Consumers request a concrete artifact shape by type
/// (for example <see cref="IContainerImageArtifact"/>) rather than switching on a kind
/// discriminator, so a controller never performs an unchecked downcast.
/// </summary>
public interface IResourceArtifact
{
    /// <summary>
    /// The identifier of the resource this artifact realizes.
    /// </summary>
    ResourceId Resource { get; }
}
