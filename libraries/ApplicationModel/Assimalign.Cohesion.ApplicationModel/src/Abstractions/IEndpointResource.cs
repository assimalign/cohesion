using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A capability interface for a resource that exposes one or more network endpoints.
/// </summary>
/// <remarks>
/// These are the <em>declared</em> (desired) endpoints. Observed or allocated endpoints —
/// OS-assigned ports, NodePort or Ingress hosts that are only known after provisioning —
/// are not carried here; they flow through the observed view on
/// <see cref="IApplicationResourceStateManager"/>.
/// </remarks>
public interface IEndpointResource : IApplicationResource
{
    /// <summary>
    /// The declared endpoints this resource exposes.
    /// </summary>
    IReadOnlyList<ResourceEndpoint> Endpoints { get; }
}
