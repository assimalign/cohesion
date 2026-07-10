using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Endpoint metadata declaring the hosts a route accepts. Attached to a route through its
/// <see cref="IRouterRouteMetadataCollection"/> and consulted by the router during candidate
/// selection: a request whose host satisfies none of the constraints skips the route entirely,
/// falling through to other candidates.
/// </summary>
/// <remarks>
/// The router resolves this metadata with last-wins semantics
/// (<see cref="IRouterRouteMetadataCollection.GetMetadata{TMetadata}"/>), so an endpoint-level
/// declaration overrides a broader (e.g. group-level) one rather than combining with it. An
/// empty <see cref="Hosts"/> list declares no constraint — the route accepts any host and ranks
/// as host-unconstrained.
/// </remarks>
public interface IRouteHostMetadata
{
    /// <summary>
    /// Gets the parsed host constraints the request host is tested against. A request
    /// satisfies the metadata when it matches <em>any</em> constraint in the list.
    /// </summary>
    IReadOnlyList<RouteHostConstraint> Hosts { get; }
}
