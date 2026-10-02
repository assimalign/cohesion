using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Authorization.Tests.TestObjects;

/// <summary>
/// An <see cref="IHttpContext"/> double for evaluating policies directly, without a pipeline (the same
/// shape the Web.Authentication tests use).
/// </summary>
internal sealed class TestHttpContext : HttpContext
{
    private TestHttpContext(TestHttpRequest request, TestHttpResponse response)
    {
        Version = HttpVersion.Http11;
        Request = request;
        Response = response;
        ConnectionInfo = HttpConnectionInfo.Empty;
        Features = new HttpFeatureCollection();
        Items = new Dictionary<string, object?>(StringComparer.Ordinal);
        RequestCancelled = CancellationToken.None;

        request.AttachContext(this);
        response.AttachContext(this);
    }

    public override HttpVersion Version { get; }
    public override TestHttpRequest Request { get; }
    public override TestHttpResponse Response { get; }
    public override HttpConnectionInfo ConnectionInfo { get; }
    public override HttpFeatureCollection Features { get; }
    public override IDictionary<string, object?> Items { get; }
    public override CancellationToken RequestCancelled { get; }

    public override void Cancel() { }
    public override Task CancelAsync() => Task.CompletedTask;
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static TestHttpContext Create() => new(new TestHttpRequest(), new TestHttpResponse());
}

internal sealed class TestHttpRequest : HttpRequest
{
    private HttpContext? _httpContext;

    public override HttpHost Host { get; set; } = HttpHost.Empty;
    public override HttpPath Path { get; set; } = HttpPath.Root;
    public override HttpMethod Method { get; set; } = HttpMethod.Get;
    public override HttpScheme Scheme { get; set; } = HttpScheme.Http;
    public override HttpQueryCollection Query { get; } = new HttpQueryCollection();
    public override HttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
    public override Stream Body { get; set; } = Stream.Null;

    public override HttpContext HttpContext => _httpContext
        ?? throw new InvalidOperationException("The HttpContext back-reference has not been attached.");

    internal void AttachContext(HttpContext context) => _httpContext ??= context;
}

internal sealed class TestHttpResponse : HttpResponse
{
    private HttpContext? _httpContext;

    public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
    public override HttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
    public override Stream Body { get; set; } = new MemoryStream();

    public override HttpContext HttpContext => _httpContext
        ?? throw new InvalidOperationException("The HttpContext back-reference has not been attached.");

    internal void AttachContext(HttpContext context) => _httpContext ??= context;
}
