using Assimalign.Cohesion.Http;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

internal class WebApplicationPipeline : IWebApplicationPipeline
{
    private readonly WebApplicationMiddleware _middleware;
    public WebApplicationPipeline(WebApplicationMiddleware middleware)
    {
        _middleware = middleware;
    }
    public async ValueTask InvokeAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        await _middleware.Invoke(context);
    }
}
