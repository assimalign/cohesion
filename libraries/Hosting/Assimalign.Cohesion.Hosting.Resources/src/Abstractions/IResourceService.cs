namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// The host service that runs a resource instance: its control plane and the transport that serves
/// the control plane.
/// </summary>
/// <remarks>
/// An area registers one with the instance's host, ahead of the services that serve traffic.
/// Starting it opens the transport, then starts <see cref="ControlPlane"/>, which publishes the
/// instance's manifest and restores the state the gateway answers with, so the instance serves only
/// once that state is in effect. Code in the same process, such as an in-process gateway, finds the
/// instance's control plane among the host's services through this interface.
/// </remarks>
public interface IResourceService : IHostService
{
    /// <summary>
    /// Gets the control plane of the resource instance the service runs.
    /// </summary>
    IResourceControlPlane ControlPlane { get; }
}
