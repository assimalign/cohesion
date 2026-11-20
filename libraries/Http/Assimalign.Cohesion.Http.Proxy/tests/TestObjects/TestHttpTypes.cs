using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Tests.TestObjects;

internal sealed class TestHttpRequest : HttpRequest
{
    public TestHttpRequest(HttpContext context)
    {
        HttpContext = context;
    }

    public override HttpHost Host { get; set; } = new("client.example");

    public override HttpPath Path { get; set; } = HttpPath.Root;

    public override HttpMethod Method { get; set; } = HttpMethod.Get;

    public override HttpScheme Scheme { get; set; } = HttpScheme.Http;

    public override HttpQueryCollection Query { get; } = new HttpQueryCollection();

    public override HttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

    public override HttpContext HttpContext { get; }

    public override Stream Body { get; set; } = Stream.Null;
}

internal sealed class TestHttpResponse : HttpResponse
{
    private readonly HttpTrailerCollection _trailers;

    public TestHttpResponse(HttpContext context, bool trailersSupported)
    {
        HttpContext = context;
        _trailers = trailersSupported ? new HttpTrailerCollection() : HttpTrailerCollection.Unsupported;
    }

    public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;

    public override HttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

    public override HttpTrailerCollection Trailers => _trailers;

    public override HttpContext HttpContext { get; }

    public override Stream Body { get; set; } = new MemoryStream();
}

/// <summary>
/// An exchange built in code: a request from 203.0.113.7 to <c>client.example</c>, with the
/// response buffered in memory.
/// </summary>
internal sealed class TestHttpContext : HttpContext
{
    public TestHttpContext(
        HttpVersion version = HttpVersion.Http11,
        CancellationToken requestCancelled = default,
        IPAddress? remoteAddress = null)
    {
        Version = version;
        Request = new TestHttpRequest(this);
        Response = new TestHttpResponse(this, trailersSupported: version != HttpVersion.Http11);
        ConnectionInfo = new HttpConnectionInfo(
            new IPEndPoint(IPAddress.Loopback, 8080),
            new IPEndPoint(remoteAddress ?? IPAddress.Parse("203.0.113.7"), 50000));
        Features = new HttpFeatureCollection();
        Items = new Dictionary<string, object?>(StringComparer.Ordinal);
        RequestCancelled = requestCancelled;
    }

    public override HttpVersion Version { get; }

    public override TestHttpRequest Request { get; }

    public override TestHttpResponse Response { get; }

    public override HttpConnectionInfo ConnectionInfo { get; }

    public override HttpFeatureCollection Features { get; }

    public override IDictionary<string, object?> Items { get; }

    public override CancellationToken RequestCancelled { get; }

    /// <summary>
    /// Gets a value indicating whether the proxy aborted the exchange.
    /// </summary>
    public bool IsCanceled { get; private set; }

    public override void Cancel()
    {
        IsCanceled = true;
    }

    public override Task CancelAsync()
    {
        IsCanceled = true;
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Reads the buffered response body as UTF-8 text.
    /// </summary>
    public string ReadResponseBody()
    {
        return System.Text.Encoding.UTF8.GetString(((MemoryStream)Response.Body).ToArray());
    }
}
