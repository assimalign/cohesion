using System.Collections.Generic;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Represents a single routable endpoint: a route pattern, the HTTP methods it accepts,
/// the handler invoked when a request matches, and the endpoint metadata attached to it.
/// </summary>
/// <remarks>
/// Matching is split into a path phase (<see cref="TryMatchPath"/>) and a method phase so a
/// router can tell a genuine no-match (404) apart from a path that matched with the wrong method
/// (405). <see cref="InboundPrecedence"/> lets the router order candidates by specificity rather
/// than by registration order.
/// </remarks>
public interface IRouterRoute
{
    /// <summary>
    /// Gets the handler mapped to the route, executed when the route is successfully matched.
    /// </summary>
    IRouterRouteHandler Handler { get; }

    /// <summary>
    /// Gets the HTTP methods accepted by the route.
    /// </summary>
    /// <remarks>
    /// An empty collection indicates the route accepts any HTTP method.
    /// </remarks>
    IReadOnlyCollection<HttpMethod> Methods { get; }

    /// <summary>
    /// Gets the inbound (request-matching) precedence of the route.
    /// </summary>
    /// <remarks>
    /// Lower values are more specific and are evaluated first. Literal segments sort ahead of
    /// constrained parameters, which sort ahead of unconstrained parameters and catch-alls.
    /// </remarks>
    decimal InboundPrecedence { get; }

    /// <summary>
    /// Gets the immutable endpoint metadata associated with the route.
    /// </summary>
    /// <remarks>
    /// Never <see langword="null"/>; routes with no declared metadata return
    /// <see cref="RouterRouteMetadataCollection.Empty"/>. This is the reflection-free
    /// seam that authorization, content negotiation, documentation and diagnostics
    /// consume to discover per-endpoint policy.
    /// </remarks>
    IRouterRouteMetadataCollection Metadata { get; }

    /// <summary>
    /// Attempts to match the request path and parameter policies against the route, ignoring the HTTP method.
    /// </summary>
    /// <param name="context">The HTTP context to evaluate.</param>
    /// <param name="values">The captured route values when the request path matches.</param>
    /// <returns><see langword="true"/> when the request path matches; otherwise <see langword="false"/>.</returns>
    bool TryMatchPath(IHttpContext context, out RouteValueDictionary values);

    /// <summary>
    /// Attempts to match the request path, parameter policies, and HTTP method against the route.
    /// </summary>
    /// <param name="context">The HTTP context to evaluate.</param>
    /// <param name="values">The captured route values when the route matches.</param>
    /// <returns><see langword="true"/> when the request path and method both match; otherwise <see langword="false"/>.</returns>
    bool TryMatch(IHttpContext context, out RouteValueDictionary values);
}
