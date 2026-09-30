using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Tests.TestObjects;

/// <summary>
/// A composing <see cref="IWebApplicationPipelineBuilder"/> that ends in the standard
/// <see cref="WebApplicationTerminal"/> and supplies a fixed application context to component factories,
/// as the Web host does.
/// </summary>
internal sealed class TestPipelineBuilder : IWebApplicationPipelineBuilder
{
    private readonly List<Func<WebApplicationMiddleware, WebApplicationMiddleware>> _middleware = new();

    public TestApplicationContext Context { get; } = new();

    public IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware)
        => Use(next => context => middleware.InvokeAsync(context, next));

    public IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        _middleware.Add(middleware);
        return this;
    }

    public IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware)
        => Use(next => middleware(Context, next));

    public IWebApplicationPipeline Build()
    {
        WebApplicationMiddleware pipeline = WebApplicationTerminal.InvokeAsync;
        for (int i = _middleware.Count - 1; i >= 0; i--)
        {
            pipeline = _middleware[i](pipeline);
        }

        return new Pipeline(pipeline);
    }

    public async Task<TestHttpContext> SendAsync(HttpMethod method, string path)
    {
        TestHttpContext context = new(method, path);
        await Build().ExecuteAsync(context);
        return context;
    }

    private sealed class Pipeline : IWebApplicationPipeline
    {
        private readonly WebApplicationMiddleware _middleware;

        public Pipeline(WebApplicationMiddleware middleware) => _middleware = middleware;

        public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default) => _middleware(context);
    }
}

internal sealed class TestApplicationContext : IWebApplicationContext
{
    public FileSystemPath? ContentRootPath => null;

    public FileSystemPath? WebRootPath => null;

    public IEnumerable<IWebApplicationMiddleware> Middleware => Array.Empty<IWebApplicationMiddleware>();

    public IEnumerable<IWebApplicationServer> Servers => Array.Empty<IWebApplicationServer>();

    public IEnumerable<IHttpFeature> Features => Array.Empty<IHttpFeature>();
}

internal sealed class TestHttpContext : HttpContext
{
    public TestHttpContext(HttpMethod method, string path)
    {
        Request = new TestHttpRequest(this) { Method = method, Path = path };
        Response = new TestHttpResponse(this);
    }

    public override HttpVersion Version => HttpVersion.Http11;
    public override TestHttpRequest Request { get; }
    public override TestHttpResponse Response { get; }
    public override HttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
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

    public override HttpHost Host { get; set; } = HttpHost.Empty;
    public override HttpPath Path { get; set; } = HttpPath.Root;
    public override HttpMethod Method { get; set; } = HttpMethod.Get;
    public override HttpScheme Scheme { get; set; } = HttpScheme.Http;
    public override HttpQueryCollection Query { get; } = new();
    public override HttpHeaderCollection Headers { get; } = new();
    public override HttpContext HttpContext { get; }
    public override Stream Body { get; set; } = Stream.Null;
}

internal sealed class TestHttpResponse : HttpResponse
{
    public TestHttpResponse(HttpContext context) => HttpContext = context;

    public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
    public override HttpHeaderCollection Headers { get; } = new();
    public override HttpContext HttpContext { get; }
    public override Stream Body { get; set; } = new MemoryStream();
}

/// <summary>A published endpoint that records its invocation.</summary>
internal sealed class TestEndpointFeature : IWebEndpointFeature
{
    public TestEndpointFeature(Func<IHttpContext, Task> endpoint) => Endpoint = context => endpoint(context);

    public string Name => nameof(IWebEndpointFeature);

    public WebApplicationMiddleware Endpoint { get; }
}
