namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Marks an external resource as artifact-free while satisfying the common controller context.
/// </summary>
internal sealed class ExternalResourceArtifact : IResourceArtifact
{
    public ExternalResourceArtifact(ResourceId resource)
    {
        Resource = resource;
    }

    public ResourceId Resource { get; }
}
