using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Forwarded.Tests.TestObjects;

/// <summary>
/// Bare-bones <see cref="IHttpContext"/> stub carrying just the surfaces the
/// <c>Effective*</c> read convention consults: the wire scheme/host on the request,
/// the transport remote endpoint, and a live feature collection.
/// </summary>
internal sealed class StubHttpContext : IHttpContext
{
    public StubHttpContext(HttpScheme scheme, HttpHost host, EndPoint? remoteEndPoint)
    {
        Request = new StubHttpRequest(this, scheme, host);
        ConnectionInfo = new HttpConnectionInfo(remoteEndPoint: remoteEndPoint);
    }

    public HttpVersion Version => HttpVersion.Http11;
    public IHttpRequest Request { get; }
    public IHttpResponse Response => null!;
    public IHttpConnectionInfo ConnectionInfo { get; }
    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
    public CancellationToken RequestCancelled => CancellationToken.None;

    public void Cancel()
    {
    }

    public Task CancelAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class StubHttpRequest : IHttpRequest
    {
        private readonly StubHttpContext _context;

        public StubHttpRequest(StubHttpContext context, HttpScheme scheme, HttpHost host)
        {
            _context = context;
            Scheme = scheme;
            Host = host;
        }

        public HttpHost Host { get; }
        public HttpPath Path => default;
        public HttpMethod Method => HttpMethod.Get;
        public HttpScheme Scheme { get; }
        public IHttpQueryCollection Query => null!;
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext => _context;
        public Stream Body => Stream.Null;
    }
}
