using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// The control plane of one resource instance. It joins the instance's host lifecycle, publishes
/// the instance's manifest when the host starts, and executes actions and restores state on request.
/// </summary>
/// <remarks>
/// The control plane is transport-neutral. A transport, such as the Web endpoint an area embeds,
/// maps its requests onto <see cref="ExecuteAsync"/> and <see cref="RestoreAsync"/>, and reads and
/// writes their content with <see cref="Serializer"/>. Both are accepted only between
/// <see cref="IHostService.StartAsync"/> and <see cref="IHostService.StopAsync"/>.
/// </remarks>
public interface IResourceControlPlane : IHostService
{
    /// <summary>
    /// Gets the resource instance this control plane serves.
    /// </summary>
    IResource Resource { get; }

    /// <summary>
    /// Gets the manifest the control plane publishes when its host starts.
    /// </summary>
    ResourceManifest Manifest { get; }

    /// <summary>
    /// Gets the content serializer the control plane writes its states' snapshots with. A transport
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
    /// <exception cref="System.InvalidOperationException">The control plane is not started.</exception>
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
    /// <exception cref="System.InvalidOperationException">The control plane is not started.</exception>
    ValueTask<ResourceActionResult> RestoreAsync(ResourceStateSnapshot snapshot, CancellationToken cancellationToken = default);
}
