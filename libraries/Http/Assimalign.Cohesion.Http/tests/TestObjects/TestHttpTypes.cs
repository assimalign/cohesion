using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Tests.TestObjects;

internal sealed class TestHttpRequest : HttpRequest
{
    public override HttpHost Host { get; set; } = HttpHost.Empty;

    public override HttpPath Path { get; set; } = HttpPath.Root;

    public override HttpMethod Method { get; set; } = HttpMethod.Get;

    public override HttpScheme Scheme { get; set; } = HttpScheme.Http;

    public override HttpQueryCollection Query { get; } = new HttpQueryCollection();

    public override HttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

    public override HttpCookieCollection Cookies { get; } = new HttpCookieCollection();

    public override Stream Body { get; set; } = Stream.Null;
}

internal sealed class TestHttpResponse : HttpResponse
{
    public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;

    public override HttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

    public override HttpCookieCollection Cookies { get; } = new HttpCookieCollection();

    public override Stream Body { get; set; } = new MemoryStream();
}

internal sealed class TestHttpContext : HttpContext
{
    public TestHttpContext(
        HttpVersion version,
        TestHttpRequest request,
        TestHttpResponse response,
        HttpConnectionInfo? connectionInfo = null,
        CancellationToken requestAborted = default)
    {
        Version = version;
        Request = request;
        Response = response;
        ConnectionInfo = connectionInfo ?? HttpConnectionInfo.Empty;
        Features = new HttpFeatureCollection();
        Items = new Dictionary<string, object?>(StringComparer.Ordinal);
        RequestAborted = requestAborted;
    }

    public override HttpVersion Version { get; }

    public override TestHttpRequest Request { get; }

    public override TestHttpResponse Response { get; }

    public override HttpConnectionInfo ConnectionInfo { get; }

    public override HttpFeatureCollection Features { get; }

    public override IDictionary<string, object?> Items { get; }

    public override CancellationToken RequestAborted { get; }

    public bool IsDisposed { get; private set; }

    public override ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
