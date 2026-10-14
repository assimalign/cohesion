using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Rewrite.Tests.TestObjects;

/// <summary>
/// A minimal in-memory <see cref="IHttpContext"/> for unit tests that drive the rewrite middleware with a
/// specific scheme, host, path, query and peer, including an <c>https</c> request and a request from a
/// trusted proxy, which the in-memory HTTP/1.1 factory cannot express.
/// </summary>
internal sealed class TestHttpContext : IHttpContext
{
    private readonly CancellationTokenSource _cancellation = new();

    public TestHttpContext(string path = "/", string? query = null, HttpScheme scheme = HttpScheme.Http, string host = "example.com")
    {
        Request = new TestHttpRequest(this)
        {
            Scheme = scheme,
            Host = new HttpHost(host),
            Path = new HttpPath(path),
            Query = new HttpQuery(query).Parse(),
        };
        Response = new TestHttpResponse(this);
    }

    public HttpVersion Version => HttpVersion.Http11;

    public TestHttpRequest Request { get; }

    public TestHttpResponse Response { get; }

    IHttpRequest IHttpContext.Request => Request;

    IHttpResponse IHttpContext.Response => Response;

    public IHttpConnectionInfo ConnectionInfo { get; set; } = HttpConnectionInfo.Empty;

    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();

    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    public CancellationToken RequestCancelled => _cancellation.Token;

    public bool Cancelled { get; private set; }

    public void Cancel() => Cancelled = true;

    public Task CancelAsync()
    {
        Cancelled = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _cancellation.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>The request half of <see cref="TestHttpContext"/>.</summary>
internal sealed class TestHttpRequest : IHttpRequest
{
    public TestHttpRequest(IHttpContext context) => HttpContext = context;

    public HttpHost Host { get; set; }

    public HttpPath Path { get; set; }

    public HttpMethod Method { get; set; } = HttpMethod.Get;

    public HttpScheme Scheme { get; set; } = HttpScheme.Http;

    public HttpQueryCollection Query { get; set; } = new();

    IHttpQueryCollection IHttpRequest.Query => Query;

    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

    public IHttpTrailerCollection Trailers { get; } = new HttpTrailerCollection();

    public IHttpContext HttpContext { get; }

    public Stream Body { get; set; } = new MemoryStream();
}

/// <summary>The response half of <see cref="TestHttpContext"/>.</summary>
internal sealed class TestHttpResponse : IHttpResponse
{
    public TestHttpResponse(IHttpContext context) => HttpContext = context;

    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;

    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

    public IHttpContext HttpContext { get; }

    public Stream Body { get; set; } = new MemoryStream();
}
