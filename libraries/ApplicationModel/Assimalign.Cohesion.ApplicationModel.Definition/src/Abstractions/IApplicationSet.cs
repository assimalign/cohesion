using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// What one gateway runs: the application resources it deploys onto its infrastructure.
/// </summary>
/// <remarks>
/// <para>
/// Each gateway has one set, declared by its own program through its
/// <see cref="IApplicationModelBuilder"/>. A gateway does not declare other gateways: a child
/// gateway names its parent and joins it at run time, so every gateway deploys on its own.
/// </para>
/// <para>
/// A resource's provider and name are unique within the set; another gateway may use the same name.
/// Once the model is built, the set accepts no changes. A set is composed on one thread and is not
/// safe for concurrent changes.
/// </para>
/// </remarks>
public interface IApplicationSet
{
    /// <summary>
    /// Gets the id of the gateway that runs this set.
    /// </summary>
    /// <remarks>The id is derived from <see cref="Name"/> by <see cref="GatewayId.FromName(GatewayName)"/>.</remarks>
    GatewayId Id => GatewayId.FromName(Name);

    /// <summary>
    /// Gets the name of the gateway that runs this set.
    /// </summary>
    GatewayName Name { get; }

    /// <summary>
    /// Gets the application resources the gateway runs, in declaration order.
    /// </summary>
    IReadOnlyCollection<IApplicationResourceDefinition> Resources { get; }

    /// <summary>
    /// Declares an application resource the gateway runs.
    /// </summary>
    /// <param name="provider">The resource's provider, such as <c>Web</c>.</param>
    /// <param name="name">The resource's name, unique among the set's resources of the provider.</param>
    /// <returns>The resource's definition, for declaring its dependencies, settings, and extensions.</returns>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="provider"/> or <paramref name="name"/> is <see langword="default"/>.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// The set already declares a resource with this provider and name, or the model is built.
    /// </exception>
    IApplicationResourceDefinition AddResource(ProviderName provider, ResourceName name);

    /// <summary>
    /// Projects the set as manifest data.
    /// </summary>
    /// <returns>The manifest, in declaration order.</returns>
    ApplicationModelManifest GetManifest();
}
