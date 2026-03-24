using Assimalign.Cohesion.Http;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Routing;

public class RouterRouteHandler : IRouterRouteHandler
{
    private readonly WebApplicationMiddleware _middleware;

    public RouterRouteHandler(WebApplicationMiddleware middleware)
    {
        _middleware = middleware;
    }

    // TODO implement cancellation token
    public Task InvokeAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        return _middleware.Invoke(context);
    }
}
