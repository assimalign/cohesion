using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.RequestTimeouts.Internal;

/// <summary>
/// The <see cref="IHttpContext"/> the timeout middleware hands downstream: a pass-through
/// decorator whose <see cref="RequestCancelled"/> is the timeout-linked token, so everything
/// below the middleware — including the router's own per-dispatch linked token and any handler
/// reading <c>context.RequestCancelled</c> — observes the timeout as a request cancellation.
/// </summary>
/// <remarks>
/// The transport's real token cannot be swapped (<see cref="IHttpContext.RequestCancelled"/> is
/// get-only and transport-owned), and tripping the transport's own cancel
/// (<see cref="IHttpContext.Cancel"/>) makes every transport reset the exchange instead of
/// sending a response — which would make the configured 504 undeliverable. Decorating the
/// context keeps cancellation application-level while the exchange stays writable; the
/// middleware still writes the timeout response on the <em>original</em> context it retained.
/// <see cref="Features"/> is likewise decorated to observe the route-match publication for
/// per-endpoint policy.
/// </remarks>
internal sealed class RequestTimeoutHttpContext : IHttpContext
{
    private readonly IHttpContext _inner;
    private readonly HttpRequestTimeoutFeature _feature;
    private readonly RequestTimeoutFeatureCollection _features;

    public RequestTimeoutHttpContext(IHttpContext inner, HttpRequestTimeoutFeature feature)
    {
        _inner = inner;
        _feature = feature;
        _features = new RequestTimeoutFeatureCollection(inner.Features, feature);
    }

    public HttpVersion Version => _inner.Version;

    public IHttpRequest Request => _inner.Request;

    public IHttpResponse Response => _inner.Response;

    public IHttpConnectionInfo ConnectionInfo => _inner.ConnectionInfo;

    public IHttpFeatureCollection Features => _features;

    public IDictionary<string, object?> Items => _inner.Items;

    public CancellationToken RequestCancelled => _feature.Token;

    public void Cancel() => _inner.Cancel();

    public Task CancelAsync() => _inner.CancelAsync();

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
