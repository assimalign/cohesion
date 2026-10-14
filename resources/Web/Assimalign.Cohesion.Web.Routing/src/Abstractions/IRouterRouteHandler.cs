using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Routing;

using Assimalign.Cohesion.Http;

/// <summary>
/// Serves a request that matched a route: the handler the router invokes for its route.
/// </summary>
public interface IRouterRouteHandler
{
    /// <summary>
    /// Serves the matched request.
    /// </summary>
    /// <param name="context">The context of the matched request.</param>
    /// <param name="cancellationToken">
    /// The token that signals the request was cancelled. <c>UseRouting</c> passes the request's
    /// <see cref="IHttpContext.RequestCancelled"/>; <see cref="IRouter.RouteAsync"/> passes its
    /// caller's token.
    /// </param>
    /// <returns>A task that completes when the request has been served.</returns>
    Task InvokeAsync(IHttpContext context, CancellationToken cancellationToken = default);
}
