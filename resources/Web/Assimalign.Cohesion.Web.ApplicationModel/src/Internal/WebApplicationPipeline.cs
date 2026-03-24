using Assimalign.Cohesion.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Internal;

internal class WebApplicationPipeline : IWebApplicationPipeline
{
    private readonly WebApplicationMiddleware _middleware;

    public WebApplicationPipeline(WebApplicationMiddleware middleware)
    {
        _middleware = middleware;
    }
    public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
