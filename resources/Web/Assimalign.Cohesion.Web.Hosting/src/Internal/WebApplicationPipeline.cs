using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

using Assimalign.Cohesion.Http;

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
