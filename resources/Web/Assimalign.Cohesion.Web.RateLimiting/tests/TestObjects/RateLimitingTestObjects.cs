using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Routing;

using EndPoint = System.Net.EndPoint;
using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using IPAddress = System.Net.IPAddress;
using IPEndPoint = System.Net.IPEndPoint;

namespace Assimalign.Cohesion.Web.RateLimiting.Tests;

/// <summary>
/// Minimal <see cref="IWebApplicationPipelineBuilder"/> that composes middleware in registration order —
/// the same shape the real <c>WebApplication</c> builder produces — without pulling in the hosting/DI
/// stack, so the middleware can be driven through its public verb.
/// </summary>
internal sealed class TestPipelineBuilder : IWebApplicationPipelineBuilder
{
    private readonly List<Func<WebApplicationMiddleware, WebApplicationMiddleware>> _middleware = new();

    public IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        _middleware.Add(middleware);
        return this;
    }

    public IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware)
        => Use(next => context => middleware.InvokeAsync(context, next));

    public IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware)
        => throw new NotSupportedException();

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

        public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
            => _middleware.Invoke(context);
    }
}

/// <summary>
/// A configurable <see cref="IHttpContext"/> test double with a real feature collection and a
/// transport-derived remote IP, so partition-key selection (client address, forwarded identity) and the
/// installed <see cref="IRateLimitingFeature"/> can be exercised directly.
/// </summary>
internal sealed class RateLimitTestContext : IHttpContext
{
    private readonly CancellationTokenSource _requestAborted = new();

    public RateLimitTestContext(IPAddress? remoteIp = null, string path = "/")
    {
        Request = new TestHttpRequest(this, path);
        Response = new TestHttpResponse(this);
        ConnectionInfo = new TestConnectionInfo(remoteIp);
    }

    public HttpVersion Version => HttpVersion.Http11;
    public IHttpRequest Request { get; }
    public IHttpResponse Response { get; }
    public IHttpConnectionInfo ConnectionInfo { get; }
    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
    public CancellationToken RequestCancelled => _requestAborted.Token;

    public bool CancelRequested { get; private set; }

    public void Cancel()
    {
        CancelRequested = true;
        _requestAborted.Cancel();
    }

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

    public string ReadResponseBody()
    {
        MemoryStream stream = (MemoryStream)Response.Body;
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

internal sealed class TestHttpRequest : IHttpRequest
{
    public TestHttpRequest(IHttpContext context, string path)
    {
        HttpContext = context;
        Path = new HttpPath(path);
    }

    public HttpHost Host => HttpHost.Empty;
    public HttpPath Path { get; }
    public HttpMethod Method => HttpMethod.Get;
    public HttpScheme Scheme => HttpScheme.Http;
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

/// <summary>A connection-info double carrying only the remote IP the partition-key selectors read.</summary>
internal sealed class TestConnectionInfo : IHttpConnectionInfo
{
    public TestConnectionInfo(IPAddress? remoteIp) => RemoteIp = remoteIp;

    public int RemotePort => 0;
    public IPAddress? RemoteIp { get; }
    public EndPoint? RemoteEndPoint => RemoteIp is null ? null : new IPEndPoint(RemoteIp, 0);
    public int LocalPort => 0;
    public IPAddress? LocalIp => null;
    public EndPoint? LocalEndPoint => null;
    public CancellationToken ConnectionAborted => CancellationToken.None;
    public void Abort() { }
    public ValueTask AbortAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// A stand-in for the router's route-match publication: installing it on the (decorated) feature
/// collection is exactly what <c>UseRouting</c> does between matching and dispatching.
/// </summary>
internal sealed class FakeRouteMatchFeature : IRouteMatchFeature
{
    private readonly IRouterRouteMetadataCollection _metadata;

    public FakeRouteMatchFeature(params object[] metadata)
        => _metadata = new Routing.Metadata.RouterRouteMetadataCollection(metadata);

    public string Name => nameof(IRouteMatchFeature);
    public IRouterRoute? Route => null;
    public RouteValueDictionary? Values => null;
    public IRouterRouteMetadataCollection Metadata => _metadata;
}

/// <summary>
/// A forwarded-headers feature reporting a proxy-vouched client identity, as
/// <c>Web.ForwardedHeaders</c> would after evaluating a trusted chain — used to prove the client-address
/// partition key follows the effective identity, not the wire peer.
/// </summary>
internal sealed class FakeForwardedFeature : IHttpForwardedFeature
{
    public FakeForwardedFeature(IPAddress effectiveClient) => RemoteIp = effectiveClient;

    public string Name => nameof(IHttpForwardedFeature);
    public HttpScheme Scheme => HttpScheme.Https;
    public HttpHost Host => HttpHost.Empty;
    public EndPoint? RemoteEndPoint => new IPEndPoint(RemoteIp!, 0);
    public IPAddress? RemoteIp { get; }
    public int RemotePort => 0;
    public HttpScheme OriginalScheme => HttpScheme.Http;
    public HttpHost OriginalHost => HttpHost.Empty;
    public EndPoint? OriginalRemoteEndPoint => null;
    public int TrustedHopCount => 1;
}

/// <summary>
/// A response-streaming feature reporting a started (head-committed) response, as the streaming feature
/// package would after the handler's first streamed write.
/// </summary>
internal sealed class FakeResponseStreamingFeature : IHttpResponseStreamingFeature
{
    public FakeResponseStreamingFeature(bool hasStarted) => HasStarted = hasStarted;

    public string Name => nameof(IHttpResponseStreamingFeature);
    public bool HasStarted { get; }
    public ValueTask StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask FlushAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask CompleteAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>Deterministic policy factories for the tests — window limiters (permit not returned on
/// dispose, so a second same-window request is rejected without any timing dependency) and a
/// concurrency limiter (permit held for the request lifetime).</summary>
internal static class TestPolicies
{
    private static readonly TimeSpan LongWindow = TimeSpan.FromHours(1);

    /// <summary>Fixed window, one permit, all requests in a single partition — deterministic reject-on-second.</summary>
    public static RateLimitingPolicy FixedWindowSingle(string key = "test")
        => RateLimitingPolicy.Create(_ => RateLimitPartition.GetFixedWindowLimiter(
            key,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 1, Window = LongWindow, QueueLimit = 0 }));

    /// <summary>Fixed window, one permit, partitioned by effective client address.</summary>
    public static RateLimitingPolicy FixedWindowPerClient()
        => RateLimitingPolicy.Create(context => RateLimitPartition.GetFixedWindowLimiter(
            RateLimitPartitionKeys.ClientAddress(context),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 1, Window = LongWindow, QueueLimit = 0 }));

    /// <summary>Concurrency, one permit, single partition — the permit is held for the request lifetime.</summary>
    public static RateLimitingPolicy ConcurrencySingle(string key = "test")
        => RateLimitingPolicy.Create(_ => RateLimitPartition.GetConcurrencyLimiter(
            key,
            _ => new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 }));
}
