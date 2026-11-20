using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Proxy.Tests.TestObjects;

/// <summary>
/// An exchange built in memory, for the requests no transport delivers, such as a <c>CONNECT</c> whose
/// host has no port: every transport refuses that target before middleware runs, so only a direct call
/// reaches the middleware's own check.
/// </summary>
internal sealed class TestHttpContext : HttpContext
{
    public TestHttpContext(HttpMethod method, string host, string path)
    {
        Request = new TestHttpRequest(this, method, host, path);
        Response = new TestHttpResponse(this);
    }

    public override HttpVersion Version => HttpVersion.Http11;
    public override HttpRequest Request { get; }
    public override HttpResponse Response { get; }
    public override HttpConnectionInfo ConnectionInfo { get; } = new();
    public override HttpFeatureCollection Features { get; } = new();
    public override IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
    public override CancellationToken RequestCancelled => CancellationToken.None;
    public override void Cancel() { }
    public override Task CancelAsync() => Task.CompletedTask;
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class TestHttpRequest : HttpRequest
{
    public TestHttpRequest(HttpContext context, HttpMethod method, string host, string path)
    {
        HttpContext = context;
        Method = method;
        Host = new HttpHost(host);
        Path = new HttpPath(path);
    }

    public override HttpContext HttpContext { get; }
    public override HttpHost Host { get; set; }
    public override HttpPath Path { get; set; }
    public override HttpMethod Method { get; set; }
    public override HttpScheme Scheme { get; set; } = HttpScheme.Http;
    public override HttpQueryCollection Query { get; } = new();
    public override HttpHeaderCollection Headers { get; } = new();
    public override Stream Body { get; set; } = Stream.Null;
}

internal sealed class TestHttpResponse : HttpResponse
{
    public TestHttpResponse(HttpContext context)
    {
        HttpContext = context;
    }

    public override HttpContext HttpContext { get; }
    public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
    public override HttpHeaderCollection Headers { get; } = new();
    public override Stream Body { get; set; } = Stream.Null;
}
