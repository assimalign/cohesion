using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Query.Tests;

/// <summary>
/// A configurable <see cref="IHttpContext"/> test double for unit-level middleware and helper
/// tests: writable request headers, a settable request body, and a readable response. Mirrors the
/// Web.Health test harness shape.
/// </summary>
internal sealed class TestHttpContext : IHttpContext
{
    public TestHttpContext(string path, HttpMethod method)
    {
        Request = new TestHttpRequest(this, path, method);
        Response = new TestHttpResponse(this);
    }

    public HttpVersion Version => HttpVersion.Http11;
    public IHttpRequest Request { get; }
    public IHttpResponse Response { get; }
    public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
    public CancellationToken RequestCancelled => CancellationToken.None;
    public void Cancel() { }
    public Task CancelAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public TestHttpRequest TypedRequest => (TestHttpRequest)Request;
}

internal sealed class TestHttpRequest : IHttpRequest
{
    public TestHttpRequest(IHttpContext context, string path, HttpMethod method)
    {
        HttpContext = context;
        Path = new HttpPath(path);
        Method = method;
    }

    public HttpHost Host => HttpHost.Empty;
    public HttpPath Path { get; }
    public HttpMethod Method { get; }
    public HttpScheme Scheme => HttpScheme.Http;
    public IHttpQueryCollection Query { get; } = new HttpQueryCollection();
    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
    public IHttpContext HttpContext { get; }
    public Stream Body { get; set; } = Stream.Null;
}

internal sealed class TestHttpResponse : IHttpResponse
{
    public TestHttpResponse(IHttpContext context) => HttpContext = context;

    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
    public IHttpContext HttpContext { get; }
    public Stream Body { get; set; } = new MemoryStream();
}
