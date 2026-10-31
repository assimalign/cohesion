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
/// <see cref="IApplicationInstanceService"/> that runs it.
/// </para>
/// <para>
/// The control plane is transport-neutral. The platform's <see cref="IApplicationResourceInstanceGatewayClient"/>
/// carries the gateway's calls to <see cref="ExecuteAsync"/> and <see cref="RestoreAsync"/>, and
/// reads their content with <see cref="Serializer"/>.
/// </para>
/// </remarks>
public interface IApplicationInstanceControlPlane
{
    /// <summary>
    /// Gets the resource instance this control plane serves.
    /// </summary>
    IApplicationInstance Resource { get; }

    /// <summary>
    /// Gets the instance's manifest: its identity and its actions. The gateway client that publishes
    /// it adds where the gateway reaches this control plane
    /// (<see cref="ApplicationInstanceManifest.ControlPlaneEndpoint"/>), which this manifest leaves
    /// unset.
    /// </summary>
    ApplicationInstanceManifest Manifest { get; }

    /// <summary>
    /// Gets the content serializer the control plane writes its states' snapshots with. A gateway
    /// client creates invocation contexts with it for the content it carries in.
    /// </summary>
    IApplicationContentSerializer Serializer { get; }

    /// <summary>
    /// Executes one invocation of an action.
    /// </summary>
    /// <remarks>
    /// After a successful write to a stateful action, <see cref="ApplicationActionResult.State"/>
    /// carries the snapshot of the state that write left.
    /// </remarks>
    /// <param name="action">The action's name.</param>
    /// <param name="context">The invocation.</param>
    /// <param name="cancellationToken">Cancels the invocation.</param>
    /// <returns>
    /// The action's result, <see cref="ApplicationActionStatus.NotFound"/> when the resource has no such
    /// action, or <see cref="ApplicationActionStatus.NotSupported"/> when the action does not accept the
    /// requested operation.
    /// </returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    ValueTask<ApplicationActionResult> ExecuteAsync(ActionName action, ApplicationActionContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a stateful action's state from a snapshot.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <returns>
    /// <see cref="ApplicationActionStatus.Succeeded"/>, <see cref="ApplicationActionStatus.NotFound"/> when
    /// the resource has no such action, <see cref="ApplicationActionStatus.NotSupported"/> when the action
    /// has no state, or <see cref="ApplicationActionStatus.Rejected"/> when the snapshot is not a value
    /// of the state's type.
    /// </returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    ValueTask<ApplicationActionResult> RestoreAsync(ApplicationActionSnapshot snapshot, CancellationToken cancellationToken = default);
}
