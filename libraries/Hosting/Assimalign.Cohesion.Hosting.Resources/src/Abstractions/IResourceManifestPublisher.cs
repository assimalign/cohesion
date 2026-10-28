using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Publishes a resource instance's manifest to the gateway that manages the resource.
/// </summary>
/// <remarks>
/// <para>
/// Publication answers with the state the instance must restore before it serves: a snapshot for
/// each stateful action written through the gateway since the resource was last deployed. An
/// instance of a resource with no such writes receives none.
/// </para>
/// <para>
/// The transport and its authentication belong to the implementation; a local gateway accepts an
/// anonymous publication. <see cref="ResourceManifestPublisher.Create"/> creates the HTTP
/// implementation. A snapshot's value must be written by the content serializer of the control
/// plane that restores it.
/// </para>
/// </remarks>
public interface IResourceManifestPublisher
{
    /// <summary>
    /// Publishes the manifest and returns the state the instance must restore.
    /// </summary>
    /// <param name="manifest">The instance's manifest.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>The snapshots to restore, at most one per action.</returns>
    ValueTask<IReadOnlyList<ResourceStateSnapshot>> PublishAsync(ResourceManifest manifest, CancellationToken cancellationToken = default);
}
