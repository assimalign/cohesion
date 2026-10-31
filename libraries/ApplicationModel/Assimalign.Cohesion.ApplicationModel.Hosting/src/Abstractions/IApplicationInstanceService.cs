using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Hosting;

/// <summary>
/// The host service that joins a resource instance to its gateway, through the platform's gateway
/// client: it publishes the instance's manifest, restores the state the gateway answers with, keeps
/// the registration alive while the host runs, and opens the control plane to the gateway's calls.
/// </summary>
/// <remarks>
/// <para>
/// An area registers one with the instance's host. It publishes and restores in
/// <see cref="IHostLifecycleService.StartingAsync"/>, which a host completes for every lifecycle
/// service before it starts any service or server. The instance therefore serves only once the
/// gateway's state is in effect, wherever the service sits among the host's registrations.
/// </para>
/// <para>
/// It starts the client in <see cref="IHostService.StartAsync"/>, after every
/// <see cref="IHostLifecycleService.StartingAsync"/>, so the gateway's calls reach the control plane
/// only once the restored state is in effect, and stops it in <see cref="IHostService.StopAsync"/>,
/// which a host calls after the servers registered after the service have drained.
/// </para>
/// </remarks>
public interface IApplicationInstanceService : IHostLifecycleService
{
    /// <summary>
    /// Gets the platform whose gateway the client reaches.
    /// </summary>
    PlatformName Platform { get; }

    /// <summary>
    /// Gets the control plane of the resource instance the service runs.
    /// </summary>
    IApplicationInstanceControlPlane ControlPlane { get; }
}
