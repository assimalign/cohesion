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
    /// Evaluates the request against the configured routes without invoking a handler or mutating the response.
    /// </summary>
    /// <param name="context">The HTTP context to evaluate.</param>
    /// <returns>
    /// A <see cref="RouteMatch"/> describing whether a route matched, whether the path matched but the method
    /// did not (405), or whether nothing matched (404).
    /// </returns>
    RouteMatch Match(IHttpContext context);

    /// <summary>
    /// Routes the request: on a successful match the mapped handler is invoked; on a method mismatch a 405
    /// response with an <c>Allow</c> header is produced; on no match the router takes no action.
    /// </summary>
    /// <param name="context">The HTTP context to route.</param>
    /// <param name="cancellationToken">A token used to cancel handler execution.</param>
    /// <returns>A task that completes when routing (and any invoked handler) completes.</returns>
    Task RouteAsync(IHttpContext context, CancellationToken cancellationToken = default);
}
