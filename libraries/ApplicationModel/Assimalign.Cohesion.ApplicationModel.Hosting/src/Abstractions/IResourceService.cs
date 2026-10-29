using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Hosting;

/// <summary>
/// The host service that joins a resource instance to its gateway: it publishes the instance's
/// manifest, restores the state the gateway answers with, and keeps the registration alive while the
/// host runs.
/// </summary>
/// <remarks>
/// <para>
/// An area registers one with the instance's host. It publishes and restores in
/// <see cref="IHostLifecycleService.StartingAsync"/>, which a host completes for every lifecycle
/// service before it starts any service or server. The instance therefore serves only once the
/// gateway's state is in effect, wherever the service sits among the host's registrations.
/// </para>
/// <para>
/// The service does not serve the control plane. An area that exposes it over a network registers
/// its server as a host service of its own, which a host starts after every
/// <see cref="IHostLifecycleService.StartingAsync"/>. Code in the same process, such as an
/// in-process gateway, finds the instance's control plane among the host's services through this
/// interface.
/// </para>
/// </remarks>
public interface IResourceService : IHostLifecycleService
{
    /// <summary>
    /// Gets the control plane of the resource instance the service runs.
    /// </summary>
    IResourceControlPlane ControlPlane { get; }
}
