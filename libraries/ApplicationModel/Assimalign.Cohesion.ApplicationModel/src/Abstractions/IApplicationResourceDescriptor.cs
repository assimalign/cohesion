using System.Collections.Generic;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Declares one application resource in an application set: a logical service, such as
/// <c>orders-api</c>, and the resources it depends on.
/// </summary>
public interface IApplicationResourceDescriptor
{
    /// <summary>
    /// Gets the id the resource's instances share.
    /// </summary>
    /// <remarks>
    /// The id is unique within <see cref="Set"/>. Across the hierarchy, a resource is identified by
    /// its set's <see cref="IApplicationSet.Id"/> and this id together.
    /// </remarks>
    ResourceId Id => ResourceId.FromName(Provider, Name);

    /// <summary>
    /// Gets the set whose gateway runs the resource.
    /// </summary>
    IApplicationSet Set { get; }

    /// <summary>
    /// Gets the resource's provider.
    /// </summary>
    ProviderName Provider { get; }

    /// <summary>
    /// Gets the resource's name.
    /// </summary>
    ResourceName Name { get; }

    /// <summary>
    /// Gets the resources this one depends on, in declaration order, each with the permissions it
    /// needs there.
    /// </summary>
    IReadOnlyList<ApplicationResourceDependency> Dependencies { get; }

    /// <summary>
    /// Declares that this resource depends on another and needs the specified permissions on it.
    /// </summary>
    /// <remarks>
    /// The gateway registers the permissions with the tenant's identity provider, which issues this
    /// resource credentials for them when it runs. Declaring the same dependency again adds the new
    /// permissions to it. The other resource may belong to any set of the same hierarchy.
    /// </remarks>
    /// <param name="resource">The resource depended on.</param>
    /// <param name="permissions">The permissions this resource needs on it; none to depend without access.</param>
    /// <returns>This descriptor.</returns>
    /// <exception cref="System.ArgumentNullException">
    /// <paramref name="resource"/> or <paramref name="permissions"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="resource"/> is this resource or belongs to another hierarchy, or a permission
    /// is <see langword="default"/>.
    /// </exception>
    IApplicationResourceDescriptor DependsOn(IApplicationResourceDescriptor resource, params ResourcePermission[] permissions);
}
