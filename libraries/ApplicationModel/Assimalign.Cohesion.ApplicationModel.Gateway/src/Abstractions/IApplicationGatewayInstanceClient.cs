using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// The gateway's client to the instances of its resources: it carries the gateway's calls to their
/// control planes, an action an operator runs on one instance and the restore that brings another
/// instance to the state a write left.
/// </summary>
/// <remarks>
/// <para>
/// The gateway half of a platform implements it, and the gateway uses the one its builder was given.
/// It reaches an instance as that instance's manifest says
/// (<see cref="ApplicationInstanceManifest.ControlPlaneEndpoint"/>), which the platform's own
/// client named when the instance published: the platform owns the channel in both directions,
/// so the address means only what the platform makes of it.
/// </para>
/// <para>
/// The client reports what the instance answered, refusals included, as an
/// <see cref="ApplicationActionResult"/>. It throws only when it gets no answer: the instance cannot
/// be reached, does not answer with a result, or names an endpoint the client cannot call. The gateway
/// then treats the instance as gone: it unregisters it, so an instance that still runs publishes
/// again and restores the gateway's state from the answer.
/// </para>
/// <para>
/// An invocation's content crosses as bytes, written by the context's serializer, as it would between
/// processes: the instance reads it with its own control plane's serializer.
/// </para>
/// </remarks>
public interface IApplicationGatewayInstanceClient
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
