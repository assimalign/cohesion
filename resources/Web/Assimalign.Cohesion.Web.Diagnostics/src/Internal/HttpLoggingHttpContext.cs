using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Diagnostics.Internal;

/// <summary>
/// Presents the capture stream to the downstream pipeline without mutating the incoming request.
/// All exchange state and lifetime operations remain owned by the original context.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("HttpLoggingHttpContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class HttpLoggingHttpContext : IHttpContext
{
    private readonly IHttpContext _inner;
    private readonly HttpLoggingHttpRequest _request;

    public HttpLoggingHttpContext(IHttpContext inner, Stream body)
    {
        _inner = inner;
        _request = new HttpLoggingHttpRequest(inner.Request, body, this);
    }

    public HttpVersion Version => _inner.Version;

    public IHttpRequest Request => _request;

    public IHttpResponse Response => _inner.Response;

    public IHttpConnectionInfo ConnectionInfo => _inner.ConnectionInfo;

    public IHttpFeatureCollection Features => _inner.Features;

    public IDictionary<string, object?> Items => _inner.Items;

    public CancellationToken RequestCancelled => _inner.RequestCancelled;

    public void Cancel() => _inner.Cancel();

    public Task CancelAsync() => _inner.CancelAsync();

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private sealed class DebugView
    {
        private readonly HttpLoggingHttpContext _target;

        public DebugView(HttpLoggingHttpContext value)
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
