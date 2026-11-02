using System;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;

/// <summary>
/// A gateway that runs resources in its own process, as the instances it runs there reach it: it
/// receives their publications and renewals, and calls the control planes attached to it directly.
/// </summary>
/// <remarks>
/// <para>
/// The gateway half of the in-process platform implements it and registers it at the endpoint it
/// names to every instance it starts (<see cref="InProcessPlatform.RegisterGateway"/>). An instance's
/// in-process client finds it there for each call, so a gateway that runs again at the same endpoint
/// is reached without the instance knowing.
/// </para>
/// <para>
/// An instance attaches its control plane once the state its publication answered with is restored,
/// and detaches it when it stops. The gateway calls only what is attached: a call for an instance
/// that is not, because it has not finished starting or has stopped, gets no answer, and the gateway
/// drops the instance until it publishes again.
/// </para>
/// </remarks>
public interface IInProcessGateway : IApplicationInstancePublisher
{
    /// <summary>
    /// Makes an instance's control plane callable by the gateway until the returned attachment is
    /// disposed.
    /// </summary>
    /// <param name="controlPlane">
    /// The control plane, which the gateway finds by the instance key its manifest names.
    /// </param>
    /// <returns>The attachment; disposing it detaches the control plane.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlPlane"/> is <see langword="null"/>.</exception>
    IDisposable Attach(IApplicationInstanceControlPlane controlPlane);
}
