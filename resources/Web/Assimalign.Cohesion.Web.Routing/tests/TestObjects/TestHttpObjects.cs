using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Routing.Tests.TestObjects;

internal sealed class TestHttpRequest : HttpRequest
{
    private HttpContext? _httpContext;

    public override HttpHost Host { get; set; } = HttpHost.Empty;

    public override HttpPath Path { get; set; } = HttpPath.Root;

    public override HttpMethod Method { get; set; } = HttpMethod.Get;

    public override HttpScheme Scheme { get; set; } = HttpScheme.Http;

    public override HttpQueryCollection Query { get; } = new HttpQueryCollection();

    public override HttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

    public override HttpContext HttpContext => _httpContext
        ?? throw new InvalidOperationException(
            "The HttpContext back-reference has not been attached. Construct the TestHttpRequest through a TestHttpContext.");

    public override Stream Body { get; set; } = Stream.Null;

    internal void AttachContext(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _httpContext ??= context;
    }
}

internal sealed class TestHttpResponse : HttpResponse
{
    private HttpContext? _httpContext;

    public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;

    public override HttpHeaderCollection Headers { get; } = new HttpHeaderCollection();

    public override HttpContext HttpContext => _httpContext
        ?? throw new InvalidOperationException(
            "The HttpContext back-reference has not been attached. Construct the TestHttpResponse through a TestHttpContext.");

    public override Stream Body { get; set; } = new MemoryStream();

    internal void AttachContext(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _httpContext ??= context;
    }
}

internal sealed class RecordingRouterRouteHandler : IRouterRouteHandler
{
    public bool WasInvoked { get; private set; }

    public int InvocationCount { get; private set; }

    public IHttpContext? Context { get; private set; }

    public Task InvokeAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        WasInvoked = true;
        InvocationCount++;
        Context = context;
        return Task.CompletedTask;
    }
}

internal sealed class TestHttpContext : HttpContext
{
    private TestHttpContext(
        HttpVersion version,
        TestHttpRequest request,
        TestHttpResponse response,
        HttpConnectionInfo? connectionInfo = null,
        CancellationToken requestCancelled = default)
    {
        Version = version;
        Request = request;
        Response = response;
        ConnectionInfo = connectionInfo ?? HttpConnectionInfo.Empty;
        Features = new HttpFeatureCollection();
        Items = new Dictionary<string, object?>(StringComparer.Ordinal);
        RequestCancelled = requestCancelled;

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

    public override void Cancel()
    {
    }

    public override Task CancelAsync()
    {
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public static TestHttpContext Create(HttpMethod method, HttpPath path)
    {
        return new TestHttpContext(
            HttpVersion.Http11,
            new TestHttpRequest
            {
                Method = method,
                Path = path,
                Scheme = HttpScheme.Http,
            },
            new TestHttpResponse());
    }
}
