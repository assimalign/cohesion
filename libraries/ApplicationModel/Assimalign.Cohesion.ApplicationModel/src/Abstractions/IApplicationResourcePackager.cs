using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The gather seam: produces or locates the deployable artifact a controller consumes for
/// a resource. Local gateways resolve an executable; container gateways resolve a pre-built
/// image. Gathering validates or loads an artifact — it does not build one.
/// </summary>
public interface IApplicationResourcePackager
{
    /// <summary>
    /// Produces or locates the deployable artifact for a resource.
    /// </summary>
    /// <param name="resource">The resource to gather an artifact for.</param>
    /// <param name="cancellationToken">Signals that gathering should be abandoned.</param>
    /// <returns>The deployable artifact for <paramref name="resource"/>.</returns>
    Task<IResourceArtifact> GatherAsync(IApplicationResource resource, CancellationToken cancellationToken = default);
}
