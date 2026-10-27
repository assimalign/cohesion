using System.Collections.Generic;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// What one gateway deploys: the application resources it runs itself and the child gateways it
/// runs, each with an application set of its own.
/// </summary>
/// <remarks>
/// <para>
/// The root set is the <see cref="IApplicationBuilder"/> of the root gateway, which deploys its
/// resources (such as the tenant's identity provider) and its child gateways. Each child gateway
/// deploys its own set in turn, so the sets form one hierarchy, and <see cref="GetManifest"/>
/// projects it as the global manifest.
/// </para>
/// <para>
/// A resource's provider and name are unique within its set; the same name may appear in another
/// set. Gateway names are unique across the whole hierarchy. Once the application is built, no set
/// accepts changes. A set is composed on one thread and is not safe for concurrent changes.
/// </para>
/// </remarks>
public interface IApplicationSet
{
    /// <summary>
    /// Gets the id of the gateway that deploys this set.
    /// </summary>
    /// <remarks>The id is derived from <see cref="Name"/> by <see cref="GatewayId.FromName(GatewayName)"/>.</remarks>
    GatewayId Id => GatewayId.FromName(Name);

    /// <summary>
    /// Gets the name of the gateway that deploys this set.
    /// </summary>
    GatewayName Name { get; }

    /// <summary>
    /// Gets the set of the gateway that deploys this one, or <see langword="null"/> for the root set.
    /// </summary>
    IApplicationSet? Parent { get; }

    /// <summary>
    /// Gets the application resources this set's gateway runs, in declaration order.
    /// </summary>
    IReadOnlyCollection<IApplicationResourceDescriptor> Resources { get; }

    /// <summary>
    /// Gets the sets of the child gateways this set's gateway runs, in declaration order.
    /// </summary>
    IReadOnlyCollection<IApplicationSet> Gateways { get; }

    /// <summary>
    /// Declares an application resource this set's gateway runs.
    /// </summary>
    /// <param name="provider">The resource's provider, such as <c>Web</c>.</param>
    /// <param name="name">The resource's name, unique among this set's resources of the provider.</param>
    /// <returns>The resource's descriptor, for declaring its dependencies.</returns>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="provider"/> or <paramref name="name"/> is <see langword="default"/>.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// The set already declares a resource with this provider and name.
    /// </exception>
    IApplicationResourceDescriptor AddResource(ProviderName provider, ResourceName name);

    /// <summary>
    /// Declares a child gateway this set's gateway runs.
    /// </summary>
    /// <param name="name">The child gateway's name, unique across the whole hierarchy.</param>
    /// <returns>The child gateway's set, for declaring what it deploys.</returns>
    /// <exception cref="System.ArgumentException"><paramref name="name"/> is <see langword="default"/>.</exception>
    /// <exception cref="System.InvalidOperationException">
    /// A gateway with this name already exists anywhere in the hierarchy.
    /// </exception>
    IApplicationSet AddGateway(GatewayName name);

    /// <summary>
    /// Projects this set and every set below it as manifest data.
    /// </summary>
    /// <returns>The manifest, in declaration order.</returns>
    ApplicationManifest GetManifest();
}
