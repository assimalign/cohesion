using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Carries a gateway's calls to the control planes of its resources' instances: an action an operator
/// runs on one instance, and the restore that brings another instance to the state a write left.
/// </summary>
/// <remarks>
/// <para>
/// A gateway calls an instance at the control-plane endpoint its manifest names
/// (<see cref="ApplicationInstanceManifest.ControlPlaneEndpoint"/>). Unless the builder sets another
/// client (<see cref="IApplicationGatewayBuilder.UseInstanceClient"/>), a gateway calls over HTTP: the
/// control-plane binding every area's control-plane server serves (ApplicationModel DESIGN.md, C5).
/// </para>
/// <para>
/// The client reports what the instance answered, refusals included, as an
/// <see cref="ApplicationActionResult"/>. It throws only when it gets no answer: the instance cannot
/// be reached, does not answer with a result, or names an endpoint the client cannot call. The gateway
/// then treats the instance as gone: it unregisters it, so an instance that still runs publishes
/// again and restores the gateway's state from the answer.
/// </para>
/// </remarks>
public interface IApplicationInstanceClient
{
    /// <summary>
    /// Runs one invocation of an action on an instance.
    /// </summary>
    /// <param name="instance">The manifest the instance published.</param>
    /// <param name="action">The action's name.</param>
    /// <param name="context">The invocation: the operation and its input, written by the context's serializer.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// What the instance answered; after a successful write to a stateful action it carries the
    /// state's snapshot (<see cref="ApplicationActionResult.State"/>).
    /// </returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="instance"/> or <paramref name="context"/> is <see langword="null"/>.</exception>
    ValueTask<ApplicationActionResult> ExecuteAsync(ApplicationInstanceManifest instance, ActionName action, ApplicationActionContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores an action's state on an instance.
    /// </summary>
    /// <param name="instance">The manifest the instance published.</param>
    /// <param name="snapshot">The snapshot a write left on another instance.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What the instance answered.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="instance"/> or <paramref name="snapshot"/> is <see langword="null"/>.</exception>
    ValueTask<ApplicationActionResult> RestoreAsync(ApplicationInstanceManifest instance, ApplicationActionSnapshot snapshot, CancellationToken cancellationToken = default);
}
