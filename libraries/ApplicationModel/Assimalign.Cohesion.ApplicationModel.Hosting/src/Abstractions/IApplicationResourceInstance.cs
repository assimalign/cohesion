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
    /// Gets the actions this instance's control plane executes.
    /// </summary>
    IReadOnlyCollection<IApplicationInstanceAction> Actions { get; }

    /// <summary>
    /// Gets the contributors to this instance's health.
    /// </summary>
    IReadOnlyCollection<IApplicationHealthContributor> HealthContributors { get; }
}
