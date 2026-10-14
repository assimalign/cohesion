using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Tests.TestObjects;

internal sealed class TestHttpRequest : HttpRequest
{
    public TestHttpRequest(HttpContext context)
    {
        HttpContext = context;
    }

    public override HttpHost Host { get; set; } = HttpHost.Empty;

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
    public TestHttpResponse(HttpContext context)
    {
        HttpContext = context;
    }

    public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;

    public override HttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

    public override HttpContext HttpContext { get; }

    public override Stream Body { get; set; } = new MemoryStream();
}

internal sealed class TestHttpContext : HttpContext
{
    public TestHttpContext(
        HttpVersion version,
        HttpConnectionInfo? connectionInfo = null,
        CancellationToken requestAborted = default)
    {
        Version = version;
        // The context constructs its request and response and passes itself to each, so the
        // back-references are fixed at construction (the HttpRequest.HttpContext contract).
        Request = new TestHttpRequest(this);
        Response = new TestHttpResponse(this);
        ConnectionInfo = connectionInfo ?? HttpConnectionInfo.Empty;
        Features = new HttpFeatureCollection();
        Items = new Dictionary<string, object?>(StringComparer.Ordinal);
        RequestCancelled = requestAborted;
    }

    public override HttpVersion Version { get; }

    public override TestHttpRequest Request { get; }

    public override TestHttpResponse Response { get; }

    public override HttpConnectionInfo ConnectionInfo { get; }

    public override HttpFeatureCollection Features { get; }

    public override IDictionary<string, object?> Items { get; }

    public override CancellationToken RequestCancelled { get; }

    public bool IsDisposed { get; private set; }

    public override void Cancel()
    {
        
    }

    public override Task CancelAsync()
    {
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
