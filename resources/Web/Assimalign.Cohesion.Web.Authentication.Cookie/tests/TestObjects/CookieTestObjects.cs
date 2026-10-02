using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Routing.Metadata;

namespace Assimalign.Cohesion.Web.Authentication.Cookie.Tests.TestObjects;

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

internal sealed class TestHttpContext : HttpContext
{
    private TestHttpContext(TestHttpRequest request, TestHttpResponse response, HttpConnectionInfo connectionInfo)
    {
        Version = HttpVersion.Http11;
        Request = request;
        Response = response;
        ConnectionInfo = connectionInfo;
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

    /// <summary>
    /// Creates a context. <paramref name="scheme"/> is the wire scheme and <paramref name="connectionInfo"/>
    /// the transport connection — set both to model a plaintext request arriving from a proxy address.
    /// </summary>
    public static TestHttpContext Create(HttpPath? path = null, HttpScheme scheme = HttpScheme.Http, HttpConnectionInfo? connectionInfo = null)
    {
        TestHttpRequest request = new() { Scheme = scheme };
        if (path is HttpPath p)
        {
            request.Path = p;
        }

        return new TestHttpContext(request, new TestHttpResponse(), connectionInfo ?? HttpConnectionInfo.Empty);
    }

    /// <summary>Marks the context's matched endpoint as an API endpoint (suppresses redirects).</summary>
    public void MarkApiEndpoint()
        => Features.Set<IRouteMatchFeature>(new TestApiRouteMatchFeature());
}

/// <summary>
/// A minimal route-match feature carrying the <see cref="ApiEndpointMetadata"/> marker so the
/// cookie handler's endpoint-metadata check resolves to "API endpoint".
/// </summary>
internal sealed class TestApiRouteMatchFeature : IRouteMatchFeature
{
    public string Name => nameof(IRouteMatchFeature);
    public IRouterRoute? Route => null;
    public RouteValueDictionary? Values => null;
    public IRouterRouteMetadataCollection Metadata { get; } =
        new RouterRouteMetadataCollection(ApiEndpointMetadata.Instance);
}

/// <summary>
/// A minimal <see cref="IWebApplicationPipelineBuilder"/> that captures the middleware a pipeline verb
/// registers, so a test can compose the real forwarded-headers middleware through its public verb and
/// run it in front of the handler over a context double. Only <see cref="Use(IWebApplicationMiddleware)"/>
/// is functional; the other overloads are inert.
/// </summary>
internal sealed class TestPipelineBuilder : IWebApplicationPipelineBuilder
{
    /// <summary>Gets the most recently registered middleware.</summary>
    public IWebApplicationMiddleware? LastMiddleware { get; private set; }

    public IWebApplicationPipelineBuilder Use(IWebApplicationMiddleware middleware)
    {
        LastMiddleware = middleware;
        return this;
    }

    public IWebApplicationPipelineBuilder Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware) => this;

    public IWebApplicationPipelineBuilder Use(Func<IWebApplicationContext, WebApplicationMiddleware, WebApplicationMiddleware> middleware) => this;

    public IWebApplicationPipeline Build() => throw new NotSupportedException();
}

/// <summary>A test <see cref="TimeProvider"/> whose current instant is settable.</summary>
internal sealed class MutableTimeProvider : TimeProvider
{
    public MutableTimeProvider(DateTimeOffset now) => UtcNow = now;

    public DateTimeOffset UtcNow { get; set; }

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan by) => UtcNow += by;
}
