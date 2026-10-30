using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Hosting;

/// <summary>
/// One running instance of a resource, its physical form: the identity it was started with, the
/// actions its control plane exposes, and the contributors to its health.
/// </summary>
/// <remarks>
/// <para>
/// Every instance of a resource shares its <see cref="IApplicationResource.Provider"/>,
/// <see cref="IApplicationResource.Name"/>, and <see cref="IApplicationResource.Id"/>;
/// <see cref="Key"/> tells one instance from another. A platform that scales a resource out runs
/// several instances, each with its own control plane.
/// </para>
/// <para>
/// The actions are bound to this instance's runtime, so executing one changes this process only.
/// Keeping a resource's instances consistent is the job of the gateway that manages them, through
/// each instance's <see cref="IApplicationInstanceControlPlane"/>.
/// </para>
/// </remarks>
public interface IApplicationResourceInstance : IApplicationResource
{
    /// <summary>
    /// Gets the key of this instance, unique to one start of one process.
    /// </summary>
    /// <remarks>
    /// A restarted process is a new instance with a new key: it starts from the state the resource's
    /// code defines and restores whatever state its publisher returns.
    /// </remarks>
    InstanceKey Key { get; }

    /// <summary>
    /// Gets the deployment that started this instance, as its environment named it, or
    /// <see langword="null"/> when no gateway deployed it.
    /// </summary>
    /// <remarks>
    /// A platform hands every instance a deployment starts the deployment's id
    /// (<see cref="AppEnvironment.Variables.Deployment"/>), and the instance repeats it in its
    /// manifest. The gateway counts a deployment ready only once an instance that repeats its id
    /// publishes, so an instance an earlier deployment left running cannot stand in for it.
    /// </remarks>
    DeploymentId? Deployment { get; }

    /// <summary>
    /// Gets the absolute URL this instance's control plane serves at, or <see langword="null"/> when
    /// the instance serves none.
    /// </summary>
    /// <remarks>
    /// A platform names the address its gateway reaches the instance at
    /// (<see cref="AppEnvironment.Variables.ControlPlaneEndpoint"/>), and the area's control-plane
    /// server listens there. The instance repeats it in its manifest, so the gateway can run an
    /// operator's action on the instance and restore a write made on another one.
    /// </remarks>
    Uri? ControlPlaneEndpoint { get; }

    /// <summary>
    /// Gets the actions this instance's control plane executes.
    /// </summary>
    IReadOnlyCollection<IApplicationInstanceAction> Actions { get; }

    /// <summary>
    /// Gets the contributors to this instance's health.
    /// </summary>
    IReadOnlyCollection<IApplicationHealthContributor> HealthContributors { get; }
}
