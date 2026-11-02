using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Hosting;

/// <summary>
/// The instance's client to its gateway, and the instance half of a platform: how a resource instance
/// the platform runs reaches the gateway that manages it. It publishes the instance's manifest and renews its registration, and it carries the
/// gateway's calls to the instance's control plane.
/// </summary>
/// <remarks>
/// <para>
/// A resource opts into a platform by adding its client to the resource's options
/// (<see cref="ApplicationInstanceOptions.UseGatewayClient"/>), usually through the verb of the
/// platform's hosting package. A resource that runs on several platforms adds one client for each, and
/// an instance uses the client of the platform its gateway names
/// (<see cref="ApplicationInstanceOptions.Platform"/>). The gateway half of the same platform receives
/// what the client sends and calls the instance through it, so the platform owns the transport in
/// both directions and the area that runs the resource owns none.
/// </para>
/// <para>
/// The client names where the gateway reaches the control plane in the manifest it publishes
/// (<see cref="ApplicationInstanceManifest.ControlPlaneEndpoint"/>), in a form only the platform
/// interprets; the manifest the control plane produces names none.
/// </para>
/// <para>
/// The instance's <see cref="IApplicationInstanceService"/> drives the client. It publishes when the
/// instance's host is starting, and calls <see cref="StartAsync"/> only once the state the publication
/// answered with is restored, so the gateway's calls never reach an instance before that state is in
/// effect. A call the gateway makes before then, or after <see cref="StopAsync"/>, gets no answer: the
/// gateway drops the instance, and an instance that still runs publishes again when it next renews.
/// </para>
/// </remarks>
public interface IApplicationInstanceGatewayClient : IApplicationInstancePublisher
{
    /// <summary>
    /// Gets the platform whose gateway the client reaches.
    /// </summary>
    PlatformName Platform { get; }

    /// <summary>
    /// Starts carrying the gateway's calls to the instance's control plane.
    /// </summary>
    /// <param name="controlPlane">The control plane of the instance whose manifest the client published.</param>
    /// <param name="cancellationToken">Cancels starting.</param>
    /// <returns>A task that completes when the gateway's calls reach the control plane.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="controlPlane"/> is <see langword="null"/>.</exception>
    ValueTask StartAsync(IApplicationInstanceControlPlane controlPlane, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops carrying the gateway's calls: a call from here gets no answer.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for a call in flight.</param>
    /// <returns>A task that completes when the client has stopped.</returns>
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
