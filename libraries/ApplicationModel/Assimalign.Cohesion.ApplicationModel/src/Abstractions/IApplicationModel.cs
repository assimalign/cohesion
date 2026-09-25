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
    /// The operation requested for this invocation.
    /// </summary>
    GatewayRunMode RunMode { get; }

    /// <summary>
    /// The stable identity of the gateway selected to realize this model.
    /// </summary>
    ResourceName GatewayIdentity { get; }

    /// <summary>
    /// The ownership identity written by platform gateways, in the form
    /// <c>&lt;application&gt;@&lt;gateway-identity&gt;</c>.
    /// </summary>
    string Owner { get; }

    /// <summary>
    /// Gets whether an existing target owned by another gateway may be adopted explicitly.
    /// </summary>
    bool Adopt { get; }

    /// <summary>
    /// Gets whether a local gateway should gracefully replace verified child processes left
    /// by an earlier gateway instance instead of re-attaching to them.
    /// </summary>
    bool RestartOrphans { get; }

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

    /// <summary>
    /// The generic, platform-neutral manifests for <see cref="Resources"/>, in declaration order.
    /// </summary>
    IReadOnlyList<ResourceManifest> Manifests { get; }

    /// <summary>
    /// The validated realization plans computed for <see cref="Resources"/> during
    /// <see cref="IApplicationBuilder.Build"/>, in declaration order.
    /// </summary>
    IReadOnlyList<ResourcePlan> Plans { get; }

    /// <summary>Gets the immutable commands claimed by this application in declaration order.</summary>
    IReadOnlyList<IResourceCommand> Commands => System.Array.Empty<IResourceCommand>();
}
