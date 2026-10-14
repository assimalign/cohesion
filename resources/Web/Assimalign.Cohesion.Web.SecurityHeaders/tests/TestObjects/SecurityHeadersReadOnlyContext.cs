using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Tests;

// Models a custom response facade that exposes a read-only head at the starting callback,
// while leaving the real transport free to serialize its own response head.
internal sealed class SecurityHeadersReadOnlyContext : IHttpContext
{
    private readonly IHttpContext _inner;

    public SecurityHeadersReadOnlyContext(IHttpContext inner)
    {
        _inner = inner;
        Response = new ReadOnlyResponse(this, inner.Response);
    }

    public HttpVersion Version => _inner.Version;
    public IHttpRequest Request => _inner.Request;
    public IHttpResponse Response { get; }
    public IHttpConnectionInfo ConnectionInfo => _inner.ConnectionInfo;
    public IHttpFeatureCollection Features => _inner.Features;
    public IDictionary<string, object?> Items => _inner.Items;
    public CancellationToken RequestCancelled => _inner.RequestCancelled;
    public void Cancel() => _inner.Cancel();
    public Task CancelAsync() => _inner.CancelAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class ReadOnlyResponse : IHttpResponse
    {
        private readonly IHttpResponse _inner;

        public ReadOnlyResponse(IHttpContext context, IHttpResponse inner)
        {
            _inner = inner;
            HttpContext = context;
            Headers = new HttpHeaderCollection(new Dictionary<HttpHeaderKey, HttpHeaderValue>(inner.Headers)).AsReadOnly();
        }

        public HttpStatusCode StatusCode { get => _inner.StatusCode; set => _inner.StatusCode = value; }
        public IHttpHeaderCollection Headers { get; }
        public IHttpContext HttpContext { get; }
        public Stream Body { get => _inner.Body; set => _inner.Body = value; }
    }
}
