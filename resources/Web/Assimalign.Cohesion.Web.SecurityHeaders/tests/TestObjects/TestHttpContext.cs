using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Tests;

/// <summary>
/// A minimal in-memory <see cref="IHttpContext"/> for unit tests that drive the security-headers middleware
/// without a transport. It publishes <see cref="Starting"/> as the exchange's
/// <see cref="IWebResponseStartingFeature"/>, so a test runs the callback the middleware registered the way a
/// server does when the response head is about to commit. Only the members the middleware touches are
/// functional; the rest are inert.
/// </summary>
internal sealed class TestHttpContext : IHttpContext
{
    public TestHttpContext()
    {
        Request = new TestHttpRequest(this);
        Response = new TestHttpResponse(this);
        Features.Set<IWebResponseStartingFeature>(Starting);
    }

    /// <summary>Gets the response-starting feature; <c>InvokeAsync</c> stages what the middleware registered.</summary>
    public SecurityHeadersStartingFeature Starting { get; } = new();

    public HttpVersion Version => default;

    public IHttpRequest Request { get; }

    public IHttpResponse Response { get; }

    public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;

    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();

    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    public CancellationToken RequestCancelled => CancellationToken.None;

    public void Cancel()
    {
    }

    public Task CancelAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>The request half of <see cref="TestHttpContext"/>: an inert <c>GET https://example.com/</c>.</summary>
internal sealed class TestHttpRequest : IHttpRequest
{
    public TestHttpRequest(IHttpContext context)
    {
        HttpContext = context;
    }

    public HttpHost Host { get; } = new("example.com");

    public HttpPath Path { get; } = new("/");

    public HttpMethod Method => HttpMethod.Get;

    public HttpScheme Scheme => HttpScheme.Https;

    public IHttpQueryCollection Query { get; } = new HttpQueryCollection();

    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

    public IHttpContext HttpContext { get; }

    public Stream Body => Stream.Null;
}

/// <summary>The response half of <see cref="TestHttpContext"/>: a <c>200</c> with mutable headers.</summary>
internal sealed class TestHttpResponse : IHttpResponse
{
    public TestHttpResponse(IHttpContext context)
    {
        HttpContext = context;
    }

    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;

    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

    public IHttpContext HttpContext { get; }

    public Stream Body { get; set; } = Stream.Null;
}
