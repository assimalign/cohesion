using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Publishes a resource instance's manifest to the gateway that manages the resource, and renews the
/// instance's registration while it runs.
/// </summary>
/// <remarks>
/// <para>
/// Publication answers with the state the instance must restore before it serves: a snapshot for
/// each stateful action written through the gateway since the resource was last deployed. An
/// instance of a resource with no such writes receives none.
/// </para>
/// <para>
/// The answer also carries a lease. An instance renews its registration before the lease lapses,
/// and a gateway drops an instance that stops renewing, so an instance that dies, even while the
/// gateway is down, leaves the gateway's registrations within one lease.
/// </para>
/// <para>
/// The gateway implements it, as its transports hand it what instances publish, and the instance half
/// of a platform implements it as the client an instance publishes through. The transport between
/// them and its authentication belong to the platform. A snapshot's value must be written by the
/// content serializer of the control plane that restores it.
/// </para>
/// </remarks>
public interface IApplicationInstancePublisher
{
    /// <summary>
    /// Publishes the manifest and returns the state the instance must restore, with its lease.
    /// </summary>
    /// <param name="manifest">The instance's manifest.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>The snapshots to restore, at most one per action, and the registration's lease.</returns>
    ValueTask<ApplicationInstancePublication> PublishAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews the instance's registration before its lease lapses.
    /// </summary>
    /// <param name="manifest">The manifest the instance published.</param>
    /// <param name="cancellationToken">Cancels the renewal.</param>
    /// <returns>
    /// <see langword="true"/> when the registration was renewed; <see langword="false"/> when the
    /// gateway does not know the instance, such as after a restart without its record. The instance
    /// then publishes again.
    /// </returns>
    ValueTask<bool> RenewAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default);
}
