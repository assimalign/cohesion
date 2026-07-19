using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.HttpsPolicy.Tests.TestObjects;

/// <summary>
/// A minimal in-memory <see cref="IHttpContext"/> for unit tests that drive the HTTPS-policy
/// middleware directly with a specific request scheme, host, path, and query — cases the in-memory
/// HTTP/1.1 test factory cannot express (an <c>https</c> request in particular). Only the members the
/// middleware touches are functional; the rest are inert.
/// </summary>
internal sealed class TestHttpContext : IHttpContext
{
    public TestHttpContext(HttpScheme scheme = HttpScheme.Http, string host = "example.com", string path = "/", HttpHeaderCollection? responseHeaders = null)
    {
        Request = new TestHttpRequest(this)
        {
            Scheme = scheme,
            Host = new HttpHost(host),
            Path = new HttpPath(path),
        };
        Response = new TestHttpResponse(this, responseHeaders ?? new HttpHeaderCollection());
    }

    public HttpVersion Version => default;

    public TestHttpRequest Request { get; }

    public TestHttpResponse Response { get; }

    IHttpRequest IHttpContext.Request => Request;

    IHttpResponse IHttpContext.Response => Response;

    public IHttpConnectionInfo ConnectionInfo => null!;

    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();

    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    public CancellationToken RequestCancelled => CancellationToken.None;

    public void Cancel()
    {
    }

    public Task CancelAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    public IHttpContext HttpContext { get; }

    public Stream Body { get; set; } = new MemoryStream();
}

/// <summary>The response half of <see cref="TestHttpContext"/>.</summary>
internal sealed class TestHttpResponse : IHttpResponse
{
    public TestHttpResponse(IHttpContext context, HttpHeaderCollection headers)
    {
        HttpContext = context;
        Headers = headers;
    }

    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;

    public IHttpHeaderCollection Headers { get; }

    public IHttpContext HttpContext { get; }

    public Stream Body { get; set; } = new MemoryStream();
}
