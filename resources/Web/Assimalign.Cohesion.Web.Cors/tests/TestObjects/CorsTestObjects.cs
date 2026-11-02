using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Routing.Metadata;

using EndPoint = System.Net.EndPoint;
using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using IPAddress = System.Net.IPAddress;

namespace Assimalign.Cohesion.Web.Cors.Tests;

/// <summary>
/// Minimal <see cref="IWebApplicationPipelineBuilder"/> that composes middleware in registration order, the
/// same shape the real <c>WebApplication</c> builder produces, without the hosting stack, so the middleware
/// is driven through its public verb. Its application context lists the settings it was given, as the
/// registrations <c>AddCors</c> makes would.
/// </summary>
internal sealed class TestPipelineBuilder : IWebApplicationPipelineBuilder
{
    private readonly List<Func<WebApplicationMiddleware, WebApplicationMiddleware>> _middleware = new();
    private readonly TestApplicationContext _context;

    public TestPipelineBuilder(params IWebApplicationSetting[] settings)
    {
        _context = new TestApplicationContext(settings);
    }

    public IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        _middleware.Add(middleware);
        return this;
    }

    public IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware)
        => Use(next => context => middleware.InvokeAsync(context, next));

    public IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware)
        => Use(next => middleware(_context, next));

    public IWebApplicationPipeline Build()
    {
        WebApplicationMiddleware pipeline = _ => Task.CompletedTask;
        for (int i = _middleware.Count - 1; i >= 0; i--)
        {
            pipeline = _middleware[i].Invoke(pipeline);
        }

        return new TestPipeline(pipeline);
    }

    private sealed class TestPipeline : IWebApplicationPipeline
    {
        private readonly WebApplicationMiddleware _middleware;

        public TestPipeline(WebApplicationMiddleware middleware) => _middleware = middleware;

        public async Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
        {
            await _middleware.Invoke(context);
            if (context.Features.Get<IWebResponseStartingFeature>() is FakeResponseStartingFeature starting)
            {
                await starting.CommitAsync();
            }
        }
    }

    private sealed class TestApplicationContext : IWebApplicationContext
    {
        public TestApplicationContext(IWebApplicationSetting[] settings) => Settings = settings;

        public FileSystemPath? ContentRootPath => null;
        public FileSystemPath? WebRootPath => null;
        public IEnumerable<IWebApplicationMiddleware> Middleware => Array.Empty<IWebApplicationMiddleware>();
        public IEnumerable<IWebApplicationServer> Servers => Array.Empty<IWebApplicationServer>();
        public IEnumerable<IHttpFeature> Features => Array.Empty<IHttpFeature>();
        public IEnumerable<IWebApplicationSetting> Settings { get; }
    }
}

/// <summary>
/// Drives <c>UseCors</c> over <see cref="TestPipelineBuilder"/>, with the setting <c>AddCors</c> would
/// register from <paramref name="configure"/>: a stage ahead of the middleware publishes the route match
/// (what <c>UseRouting</c> does), and a stage behind it records whether the request continued and runs an
/// optional downstream action.
/// </summary>
internal static class CorsPipeline
{
    public static async Task<bool> InvokeAsync(
        CorsTestContext context,
        Action<CorsOptions>? configure,
        IRouteMatchFeature? match = null,
        Func<IHttpContext, Task>? downstream = null)
    {
        bool continued = false;
        CorsBuilder cors = new();
        configure?.Invoke(cors.Options);
        TestPipelineBuilder builder = new(cors.Build());

        if (match is not null)
        {
            builder.Use(next => ctx =>
            {
                ctx.Features.Set(match);
                return next.Invoke(ctx);
            });
        }

        builder.UseCors();
        builder.Use(next => async ctx =>
        {
            continued = true;
            if (downstream is not null)
            {
                await downstream(ctx);
            }
        });

        await builder.Build().ExecuteAsync(context, CancellationToken.None);
        return continued;
    }
}

/// <summary>
/// A configurable <see cref="IHttpContext"/> double with a real feature collection and real header
/// collections, so CORS request headers can be staged and response headers read back.
/// </summary>
internal sealed class CorsTestContext : IHttpContext
{
    private readonly CancellationTokenSource _requestAborted = new();

    public CorsTestContext(HttpMethod method, string path = "/items", string? origin = null)
    {
        Request = new TestHttpRequest(this, method, path);
        Response = new TestHttpResponse(this);
        Features.Set<IWebResponseStartingFeature>(new FakeResponseStartingFeature());

        if (origin is not null)
        {
            Request.Headers[HttpHeaderKey.Origin] = origin;
        }
    }

    /// <summary>Creates a CORS preflight: <c>OPTIONS</c> with <c>Origin</c> and <c>Access-Control-Request-Method</c>.</summary>
    public static CorsTestContext Preflight(string origin, string requestedMethod, string? requestedHeaders = null, string path = "/items")
    {
        CorsTestContext context = new(HttpMethod.Options, path, origin);
        context.Request.Headers[HttpHeaderKey.AccessControlRequestMethod] = requestedMethod;

        if (requestedHeaders is not null)
        {
            context.Request.Headers[HttpHeaderKey.AccessControlRequestHeaders] = requestedHeaders;
        }

        return context;
    }

    public HttpVersion Version => HttpVersion.Http11;
    public IHttpRequest Request { get; }
    public IHttpResponse Response { get; }
    public IHttpConnectionInfo ConnectionInfo { get; } = new TestConnectionInfo();
    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
    public CancellationToken RequestCancelled => _requestAborted.Token;

    /// <summary>Gets a response header value, or <see langword="null"/> when the header is absent.</summary>
    public string? ResponseHeader(HttpHeaderKey key)
        => Response.Headers.TryGetValue(key, out HttpHeaderValue value) ? value.Value : null;

    public void Cancel() => _requestAborted.Cancel();

    public Task CancelAsync()
    {
        Cancel();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _requestAborted.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class TestHttpRequest : IHttpRequest
{
    public TestHttpRequest(IHttpContext context, HttpMethod method, string path)
    {
        HttpContext = context;
        Method = method;
        Path = new HttpPath(path);
    }

    public HttpHost Host => HttpHost.Empty;
    public HttpPath Path { get; }
    public HttpMethod Method { get; }
    public HttpScheme Scheme => HttpScheme.Https;
    public IHttpQueryCollection Query { get; } = new HttpQueryCollection();
    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
    public IHttpContext HttpContext { get; }
    public Stream Body => Stream.Null;
}

internal sealed class TestHttpResponse : IHttpResponse
{
    public TestHttpResponse(IHttpContext context) => HttpContext = context;

    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
    public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
    public IHttpContext HttpContext { get; }
    public Stream Body { get; set; } = new MemoryStream();
}

internal sealed class TestConnectionInfo : IHttpConnectionInfo
{
    public int RemotePort => 0;
    public IPAddress? RemoteIp => IPAddress.Loopback;
    public EndPoint? RemoteEndPoint => null;
    public int LocalPort => 0;
    public IPAddress? LocalIp => null;
    public EndPoint? LocalEndPoint => null;
    public CancellationToken ConnectionAborted => CancellationToken.None;
    public void Abort() { }
    public ValueTask AbortAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// A stand-in for the endpoint <c>UseRouting</c> publishes. Set <see cref="IsPreflight"/> to model the
/// candidate routing publishes for a CORS preflight, and <see cref="Route"/> to model the route routing
/// matched for an <c>OPTIONS</c> request itself.
/// </summary>
internal sealed class FakeRouteMatchFeature : IRouteMatchFeature
{
    private readonly IRouterRouteMetadataCollection _metadata;

    public FakeRouteMatchFeature(params object[] metadata)
        => _metadata = new RouterRouteMetadataCollection(metadata);

    public string Name => nameof(IRouteMatchFeature);
    public bool IsPreflight { get; init; }
    public IRouterRoute? Route { get; init; }
    public RouteValueDictionary? Values => null;
    public IRouterRouteMetadataCollection Metadata => _metadata;
}

/// <summary>
/// Models the server's single callback pass at response commit, including a commit inside a handler.
/// Refused-head replacement and preserved-header behavior are covered through the real transport.
/// </summary>
internal sealed class FakeResponseStartingFeature : IWebResponseStartingFeature
{
    private readonly List<(Func<object?, Task> Callback, object? State)> _callbacks = new();
    private bool _invoked;

    public string Name => nameof(IWebResponseStartingFeature);

    public void OnStarting(Func<object?, Task> callback, object? state, IReadOnlyCollection<HttpHeaderKey>? preservedHeaders = null)
    {
        if (_invoked)
        {
            throw new InvalidOperationException("The response-starting callbacks already ran.");
        }

        _callbacks.Add((callback, state));
    }

    public async Task CommitAsync()
    {
        if (_invoked)
        {
            return;
        }

        _invoked = true;
        for (int index = _callbacks.Count - 1; index >= 0; index--)
        {
            await _callbacks[index].Callback(_callbacks[index].State);
        }
    }
}
