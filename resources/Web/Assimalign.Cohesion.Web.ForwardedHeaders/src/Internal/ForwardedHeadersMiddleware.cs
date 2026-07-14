using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.ForwardedHeaders.Internal;

/// <summary>
/// The forwarded-headers middleware: runs the trust-model resolution for the exchange
/// and attaches the resulting <see cref="IHttpForwardedFeature"/> to
/// <see cref="IHttpContext.Features"/>. It only ever <em>reads</em> the request — raw
/// headers, <see cref="IHttpRequest.Scheme"/>/<see cref="IHttpRequest.Host"/>, and
/// <see cref="IHttpContext.ConnectionInfo"/> are never mutated. The feature is attached
/// on every exchange (with a zero trusted-hop count when nothing resolved), so
/// downstream consumers get a uniform read.
/// </summary>
internal sealed class ForwardedHeadersMiddleware : IWebApplicationMiddleware
{
    private readonly ForwardedHeadersResolver _resolver;

    public ForwardedHeadersMiddleware(ForwardedHeadersResolver resolver)
    {
        _resolver = resolver;
    }

    public Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        context.Features.Set(_resolver.Resolve(context));
        return next.Invoke(context);
    }
}
