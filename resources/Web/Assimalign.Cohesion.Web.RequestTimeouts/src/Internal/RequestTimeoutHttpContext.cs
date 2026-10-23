using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.RequestTimeouts.Internal;

/// <summary>
/// The <see cref="IHttpContext"/> the timeout middleware hands downstream: a pass-through
/// decorator whose <see cref="RequestCancelled"/> is the timeout-linked token, so everything
/// below the middleware — including the endpoint the pipeline terminal runs with
/// <c>context.RequestCancelled</c> and any handler reading it — observes the timeout as a
/// request cancellation.
/// </summary>
/// <remarks>
/// The transport's real token cannot be swapped (<see cref="IHttpContext.RequestCancelled"/> is
/// get-only and transport-owned), and tripping the transport's own cancel
/// (<see cref="IHttpContext.Cancel"/>) makes every transport reset the exchange instead of
/// sending a response — which would make the configured 504 undeliverable. Decorating the
/// context keeps cancellation application-level while the exchange stays writable; the
/// middleware still writes the timeout response on the <em>original</em> context it retained.
/// Every other member, <see cref="Features"/> included, forwards to the original context, so
/// shared exchange state never forks.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("RequestTimeoutHttpContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class RequestTimeoutHttpContext : IHttpContext
{
    private readonly IHttpContext _inner;
    private readonly RequestTimeoutFeature _feature;

    public RequestTimeoutHttpContext(IHttpContext inner, RequestTimeoutFeature feature)
    {
        _inner = inner;
        _feature = feature;
    }

    public HttpVersion Version => _inner.Version;

    public IHttpRequest Request => _inner.Request;

    public IHttpResponse Response => _inner.Response;

    public IHttpConnectionInfo ConnectionInfo => _inner.ConnectionInfo;

    public IHttpFeatureCollection Features => _inner.Features;

    public IDictionary<string, object?> Items => _inner.Items;

    public CancellationToken RequestCancelled => _feature.Token;

    public void Cancel() => _inner.Cancel();

    public Task CancelAsync() => _inner.CancelAsync();

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private sealed class DebugView
    {
        private readonly RequestTimeoutHttpContext _target;

        public DebugView(RequestTimeoutHttpContext value)
        {
            _target = value;
        }

        public HttpVersion Version => _target.Version;
        public IHttpRequest Request => _target.Request;
        public IHttpResponse Response => _target.Response;
        public IHttpConnectionInfo ConnectionInfo => _target.ConnectionInfo;
        public IHttpFeatureCollection Features => _target.Features;
        public IDictionary<string, object?> Items => _target.Items;
        public CancellationToken RequestCancelled => _target.RequestCancelled;
    }
}
