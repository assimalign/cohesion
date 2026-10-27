using System.Collections.Generic;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// One running instance of a resource: the identity it was started with, the actions its control
/// plane exposes, and the contributors to its health.
/// </summary>
/// <remarks>
/// <para>
/// Every instance of a resource shares its <see cref="Provider"/>, <see cref="Name"/>, and
/// <see cref="Id"/>; <see cref="Key"/> tells one instance from another. A platform that scales a
/// resource out runs several instances, each with its own control plane.
/// </para>
/// <para>
/// The actions are bound to this instance's runtime, so executing one changes this process only.
/// Keeping a resource's instances consistent is the job of the gateway that manages them, through
/// each instance's <see cref="IResourceControlPlane"/>.
/// </para>
/// </remarks>
public interface IResource
{
    /// <summary>
    /// Gets the id every instance of this resource shares.
    /// </summary>
    /// <remarks>
    /// The id is derived from <see cref="Provider"/> and <see cref="Name"/> by
    /// <see cref="ResourceId.FromName(ProviderName, ResourceName)"/>.
    /// </remarks>
    ResourceId Id => ResourceId.FromName(Provider, Name);

    /// <summary>
    /// Gets the resource's provider: its kind, such as <c>Web</c> or <c>Database.Sql</c>.
    /// </summary>
    ProviderName Provider { get; }

    /// <summary>
    /// Gets the name every instance of this resource shares, such as <c>orders-api</c>. It is unique
    /// among one gateway's resources of the same provider.
    /// </summary>
    ResourceName Name { get; }

    /// <summary>
    /// Gets the key of this instance, unique to one start of one process.
    /// </summary>
    /// <remarks>
    /// A restarted process is a new instance with a new key: it starts from the state the resource's
    /// code defines and restores whatever state its publisher returns.
    /// </remarks>
    ResourceInstanceKey Key { get; }

    /// <summary>
    /// Gets the actions this instance's control plane executes.
    /// </summary>
    IReadOnlyCollection<IResourceAction> Actions { get; }

    /// <summary>
    /// Gets the contributors to this instance's health.
    /// </summary>
    IReadOnlyCollection<IResourceHealthContributor> HealthContributors { get; }
}
