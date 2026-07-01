using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The immutable desired state of an application — the architecture graph: a named set
/// of resources, the environment they are realized into, and the dependency edges
/// between them.
/// </summary>
/// <remarks>
/// <see cref="Descriptors"/> is the single source of truth: the dependency edges live
/// there and a gateway topologically sorts them. <see cref="Resources"/> is a read-only
/// one-to-one projection of <see cref="Descriptors"/> for convenience. The mutable
/// working collection used while authoring lives on <see cref="IApplicationBuilder"/>,
/// not here.
/// </remarks>
public interface IApplicationModel
{
    /// <summary>
    /// A stable, human-meaningful name for the application. Gateways use it as the
    /// Kubernetes namespace, the local log-prefix root, and the registry repository prefix.
    /// </summary>
    ApplicationName Name { get; }

    /// <summary>
    /// The environment this application is being realized into.
    /// </summary>
    IApplicationEnvironment Environment { get; }

    /// <summary>
    /// The dependency descriptors — authoritative. Each descriptor wraps a resource and
    /// the resources it must be realized after.
    /// </summary>
    IReadOnlyList<IApplicationResourceDescriptor> Descriptors { get; }

    /// <summary>
    /// A read-only projection of the resources composing the application, in declaration
    /// order. Equivalent to <c>Descriptors.Select(d =&gt; d.Resource)</c>; the invariant
    /// <c>Resources.Count == Descriptors.Count</c> holds with a one-to-one correspondence.
    /// </summary>
    IReadOnlyList<IApplicationResource> Resources { get; }
}
