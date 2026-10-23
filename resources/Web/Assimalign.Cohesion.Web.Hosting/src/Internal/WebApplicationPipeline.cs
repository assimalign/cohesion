using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

using Assimalign.Cohesion.Http;

[global::System.Diagnostics.DebuggerDisplay("WebApplicationPipeline")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal class WebApplicationPipeline : IWebApplicationPipeline
{
    private readonly WebApplicationMiddleware _middleware;
    
    public WebApplicationPipeline(WebApplicationMiddleware middleware)
    {
        _middleware = middleware;
    }

    public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        // Middleware delegates take no token; they observe cancellation through
        // context.RequestCancelled, which the transports link to the connection's lifetime. The
        // caller's token gates the start instead: an execution cancelled before it begins runs no
        // middleware.
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        return _middleware.Invoke(context);
    }

    private sealed class DebugView
    {
        public DebugView(WebApplicationPipeline value)
        {
        }
    }
}
