using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Wraps a resource together with the dependency edges that determine the order in
/// which a gateway realizes the graph.
/// </summary>
public interface IApplicationResourceDescriptor
{
    /// <summary>
    /// The resource this descriptor wraps.
    /// </summary>
    IApplicationResource Resource { get; }

    /// <summary>
    /// The resources this resource must be realized after, in declaration order.
    /// </summary>
    IReadOnlyList<IApplicationResourceDescriptor> Dependencies { get; }

    /// <summary>
    /// Declares that this resource depends on (must be realized after) another resource.
    /// </summary>
    /// <param name="resource">The resource that must be realized first.</param>
    /// <returns>This descriptor, for chaining.</returns>
    IApplicationResourceDescriptor DependsOn(IApplicationResourceDescriptor resource);

    /// <summary>
    /// Declares that this resource depends on (must be realized after) all of the given resources.
    /// </summary>
    /// <param name="resources">The resources that must be realized first.</param>
    /// <returns>This descriptor, for chaining.</returns>
    IApplicationResourceDescriptor DependsOn(params IApplicationResourceDescriptor[] resources);
}
