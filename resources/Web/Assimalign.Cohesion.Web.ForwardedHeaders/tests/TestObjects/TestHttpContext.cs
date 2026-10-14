using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

using EndPoint = System.Net.EndPoint;

namespace Assimalign.Cohesion.Web.ForwardedHeaders.Tests.TestObjects;

internal sealed class TestHttpContext : HttpContext
{
    public TestHttpContext(EndPoint? peer)
    {
        ConnectionInfo = new HttpConnectionInfo(remoteEndPoint: peer);
        Request = new TestHttpRequest(this);
        Response = new TestHttpResponse(this);
        Features.Set<IWebRequestIdFeature>(RequestIdFeature);
    }

    public TestRequestIdFeature RequestIdFeature { get; } = new();
    public override HttpVersion Version => HttpVersion.Http11;
    public override HttpRequest Request { get; }
    public override HttpResponse Response { get; }
    public override HttpConnectionInfo ConnectionInfo { get; }
    public override HttpFeatureCollection Features { get; } = new();
    public override IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
    public override CancellationToken RequestCancelled => CancellationToken.None;
    public override void Cancel() { }
    public override Task CancelAsync() => Task.CompletedTask;
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class TestHttpRequest : HttpRequest
{
    public TestHttpRequest(HttpContext context) => HttpContext = context;

    public override HttpContext HttpContext { get; }
    public override HttpHost Host { get; set; } = new("wire.example");
    public override HttpPath Path { get; set; } = new("/private-path");
    public override HttpMethod Method { get; set; } = HttpMethod.Get;
    public override HttpScheme Scheme { get; set; } = HttpScheme.Http;
    public override HttpQueryCollection Query { get; } = new();
    public override HttpHeaderCollection Headers { get; } = new();
    public override Stream Body { get; set; } = Stream.Null;
}

internal sealed class TestHttpResponse : HttpResponse
{
    public TestHttpResponse(HttpContext context) => HttpContext = context;

    public override HttpContext HttpContext { get; }
    public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
    public override HttpHeaderCollection Headers { get; } = new();
    public override Stream Body { get; set; } = Stream.Null;
}

internal sealed class TestRequestIdFeature : IWebRequestIdFeature
{
    public ActivityTraceId Id { get; } = ActivityTraceId.CreateRandom();
    public int Reads { get; private set; }
    public string Name => nameof(TestRequestIdFeature);
    public ActivityTraceId RequestId
    {
        get
        {
            Reads++;
            return Id;
        }
    }
}
