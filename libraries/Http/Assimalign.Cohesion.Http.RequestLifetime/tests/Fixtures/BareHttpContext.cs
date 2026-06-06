using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.RequestLifetime.Tests;

/// <summary>
/// Minimal <see cref="IHttpContext"/> test double exposing a real feature
/// collection so the request-lifetime extension can install and resolve its
/// feature. Request/response members are inert stubs (the lifetime surface
/// never touches them).
/// </summary>
internal sealed class BareHttpContext : IHttpContext
{
    public BareHttpContext()
    {
        Request = new BareHttpRequest(this);
        Response = new BareHttpResponse(this);
    }

    public HttpVersion Version => HttpVersion.Http11;
    public IHttpRequest Request { get; }
    public IHttpResponse Response { get; }
    public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
    public CancellationToken RequestCancelled => CancellationToken.None;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class BareHttpRequest : IHttpRequest
    {
        public BareHttpRequest(IHttpContext context) => HttpContext = context;

        public HttpHost Host => HttpHost.Empty;
        public HttpPath Path => HttpPath.Root;
        public HttpMethod Method => HttpMethod.Get;
        public HttpScheme Scheme => HttpScheme.Http;
        public IHttpQueryCollection Query { get; } = new HttpQueryCollection();
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext { get; }
        public Stream Body => Stream.Null;
    }

    private sealed class BareHttpResponse : IHttpResponse
    {
        public BareHttpResponse(IHttpContext context) => HttpContext = context;

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext { get; }
        public Stream Body { get; set; } = Stream.Null;
    }
}
