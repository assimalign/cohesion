using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Resolves a container image reference into the deployable image artifact for a resource.
/// </summary>
public interface IImageRealizer
{
    /// <summary>
    /// Realizes <paramref name="imageReference"/> for <paramref name="resource"/>.
    /// </summary>
    /// <param name="resource">The resource that will consume the image.</param>
    /// <param name="imageReference">The digest-pinned image reference to realize.</param>
    /// <param name="cancellationToken">Signals that image realization should stop.</param>
    /// <returns>The realized container image artifact.</returns>
    Task<IContainerImageArtifact> RealizeAsync(
        ResourceId resource,
        string imageReference,
        CancellationToken cancellationToken = default);
}
