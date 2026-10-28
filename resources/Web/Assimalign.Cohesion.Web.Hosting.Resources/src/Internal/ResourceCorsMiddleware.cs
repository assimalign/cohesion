using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Internal;

/// <summary>
/// The pipeline's CORS middleware for a managed resource: it forwards each request to the CORS
/// middleware of the policy currently in effect, which a write or restore replaces.
/// </summary>
/// <remarks>
/// A replacement takes effect for the next request; a request in flight finishes under the
/// policy it started with.
/// </remarks>
internal sealed class ResourceCorsMiddleware : IWebApplicationMiddleware
{
    private IWebApplicationMiddleware _current;

    public ResourceCorsMiddleware(IWebApplicationMiddleware current)
    {
        _current = current;
    }

    public void Replace(IWebApplicationMiddleware middleware)
    {
        Volatile.Write(ref _current, middleware);
    }

    public Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        return Volatile.Read(ref _current).InvokeAsync(context, next);
    }
}
