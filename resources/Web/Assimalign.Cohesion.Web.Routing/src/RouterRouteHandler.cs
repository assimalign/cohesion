using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Serves a matched route with a terminal <see cref="WebApplicationMiddleware"/>.
/// </summary>
public class RouterRouteHandler : IRouterRouteHandler
{
    private readonly WebApplicationMiddleware _middleware;

    /// <summary>
    /// Initializes a handler that serves its route with <paramref name="middleware"/>.
    /// </summary>
    /// <param name="middleware">The terminal middleware invoked when the route matches.</param>
    /// <exception cref="ArgumentNullException"><paramref name="middleware"/> is <see langword="null"/>.</exception>
    public RouterRouteHandler(WebApplicationMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        _middleware = middleware;
    }

    /// <summary>
    /// Invokes the middleware for the matched request.
    /// </summary>
    /// <param name="context">The context of the matched request.</param>
    /// <param name="cancellationToken">
    /// The token that signals the request was cancelled; <c>UseRouting</c> passes the request's
    /// <see cref="IHttpContext.RequestCancelled"/>. The middleware delegate takes no token, so a token
    /// that is already cancelled keeps it from starting, and while it runs it observes cancellation
    /// through <see cref="IHttpContext.RequestCancelled"/>.
    /// </param>
    /// <returns>
    /// A task that completes when the middleware has served the request, or a cancelled task when
    /// <paramref name="cancellationToken"/> was already cancelled.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public Task InvokeAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        return _middleware.Invoke(context);
    }
}
