namespace Assimalign.Cohesion.ApplicationModel.Internal;

internal sealed class GenericPlannedResource<TOptions> : PlannedResource
    where TOptions : class, IResourceOptions
{
    public GenericPlannedResource(ResourceManifest manifest, TOptions options)
        : base(manifest, options)
    {
    }
}
