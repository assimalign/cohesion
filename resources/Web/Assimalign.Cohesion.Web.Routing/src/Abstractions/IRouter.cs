using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Routing;

using Assimalign.Cohesion.Http;

/// <summary>
/// Evaluates a set of routes against incoming HTTP requests, honoring route precedence and HTTP method semantics.
/// </summary>
public interface IRouter
{
    /// <summary>
    /// Gets the routes associated with the router, in registration order.
    /// </summary>
    IEnumerable<IRouterRoute> Routes { get; }

    /// <summary>
    /// Gets the link generator that builds outbound URLs (paths and absolute URIs) from the
    /// router's routes.
    /// </summary>
    /// <remarks>
    /// The generator is built together with the router, so route-name registration errors
    /// (duplicate names) surface when the route table is built, not at request time.
    /// </remarks>
    ILinkGenerator LinkGenerator { get; }

    /// <summary>
    /// Evaluates the request against the configured routes without invoking a handler or mutating the response.
    /// </summary>
    /// <param name="context">The HTTP context to evaluate.</param>
    /// <returns>
    /// A <see cref="RouteMatch"/> describing whether a route matched, whether the path matched but the method
    /// did not (405), or whether nothing matched (404).
    /// </returns>
    RouteMatch Match(IHttpContext context);

    /// <summary>
    /// Evaluates the request against the configured routes as though it used <paramref name="method"/>,
    /// without invoking a handler or mutating the response.
    /// </summary>
    /// <remarks>
    /// Resolves the endpoint a request <em>would</em> reach with another method. Routing uses it to find
    /// the candidate endpoint of a CORS preflight: an <c>OPTIONS</c> request that names the method of the
    /// actual request in <c>Access-Control-Request-Method</c>. Host constraints, precedence and the
    /// <c>HEAD</c>-to-<c>GET</c> rule apply exactly as in <see cref="Match(IHttpContext)"/>.
    /// </remarks>
    /// <param name="context">The HTTP context to evaluate.</param>
    /// <param name="method">The method to match with in place of the request's own method.</param>
    /// <returns>A <see cref="RouteMatch"/> for <paramref name="method"/>.</returns>
    RouteMatch Match(IHttpContext context, HttpMethod method);

    /// <summary>
    /// Routes the request: on a successful match the mapped handler is invoked; on a method mismatch a 405
    /// response with an <c>Allow</c> header is produced; on no match the router takes no action.
    /// </summary>
    /// <param name="context">The HTTP context to route.</param>
    /// <param name="cancellationToken">A token used to cancel handler execution.</param>
    /// <returns>A task that completes when routing (and any invoked handler) completes.</returns>
    Task RouteAsync(IHttpContext context, CancellationToken cancellationToken = default);
}
