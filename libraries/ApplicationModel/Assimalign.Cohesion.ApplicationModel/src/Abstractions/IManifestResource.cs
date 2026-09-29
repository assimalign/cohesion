namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A declared application resource backed by the generic resource manifest contract.
/// </summary>
public interface IManifestResource : IApplicationResource
{
    /// <summary>Gets the build-produced facts that describe the resource.</summary>
    ResourceManifest Manifest { get; }
}
