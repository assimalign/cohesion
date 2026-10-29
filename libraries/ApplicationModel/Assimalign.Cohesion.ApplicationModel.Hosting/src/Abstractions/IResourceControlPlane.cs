using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Hosting;

/// <summary>
/// The control plane of one resource instance: the manifest it publishes and the actions its gateway
/// executes on it.
/// </summary>
/// <remarks>
/// <para>
/// The control plane has no lifecycle of its own and accepts invocations and restores from the
/// moment it is created. Joining the instance's host and gateway is the work of the
/// <see cref="IResourceService"/> that runs it.
/// </para>
/// <para>
/// The control plane is transport-neutral. A server, such as the HTTP listener an area registers as
/// a host service of its own, maps its requests onto <see cref="ExecuteAsync"/> and
/// <see cref="RestoreAsync"/>, and reads and writes their content with <see cref="Serializer"/>.
/// </para>
/// </remarks>
public interface IResourceControlPlane
{
    /// <summary>
    /// Gets the resource instance this control plane serves.
    /// </summary>
    IApplicationResourceInstance Resource { get; }

    /// <summary>
    /// Gets the manifest the instance publishes to its gateway.
    /// </summary>
    ResourceManifest Manifest { get; }

    /// <summary>
    /// Gets the content serializer the control plane writes its states' snapshots with. A server
    /// creates invocation contexts with it and writes the manifest and results with it.
    /// </summary>
    IResourceContentSerializer Serializer { get; }

    /// <summary>
    /// Executes one invocation of an action.
    /// </summary>
    /// <remarks>
    /// After a successful write to a stateful action, <see cref="ResourceActionResult.State"/>
    /// carries the snapshot of the state that write left.
    /// </remarks>
    /// <param name="action">The action's name.</param>
    /// <param name="context">The invocation.</param>
    /// <param name="cancellationToken">Cancels the invocation.</param>
    /// <returns>
    /// The action's result, <see cref="ResourceActionStatus.NotFound"/> when the resource has no such
    /// action, or <see cref="ResourceActionStatus.NotSupported"/> when the action does not accept the
    /// requested operation.
    /// </returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    ValueTask<ResourceActionResult> ExecuteAsync(ResourceActionName action, ResourceActionContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a stateful action's state from a snapshot.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <returns>
    /// <see cref="ResourceActionStatus.Succeeded"/>, <see cref="ResourceActionStatus.NotFound"/> when
    /// the resource has no such action, <see cref="ResourceActionStatus.NotSupported"/> when the action
    /// has no state, or <see cref="ResourceActionStatus.Rejected"/> when the snapshot is not a value
    /// of the state's type.
    /// </returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    ValueTask<ResourceActionResult> RestoreAsync(ResourceStateSnapshot snapshot, CancellationToken cancellationToken = default);
}
