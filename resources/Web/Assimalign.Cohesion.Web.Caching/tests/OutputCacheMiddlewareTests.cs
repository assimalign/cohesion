using System;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Caching.Internal;
using Assimalign.Cohesion.Web.Caching.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Caching.Tests;

/// <summary>
/// Unit coverage for the middleware's cache-or-bypass decisions driven directly over an in-memory
/// context double: the hit path skips downstream and stamps <c>Age</c>; the request/response bypass
/// matrix (authenticated request, <c>Set-Cookie</c> response, request <c>no-store</c>, non-200 status,
/// over-cap body) never serves a stale or shared representation; and the per-endpoint policy and route
/// values come from the endpoint published ahead of the middleware, never from a second route match.
/// </summary>
public class OutputCacheMiddlewareTests
{
    private static OutputCacheMiddleware CreateMiddleware(out InMemoryOutputCacheStore store, Action<OutputCacheOptions>? configure = null)
    {
        OutputCacheOptions options = new();
        options.AddBasePolicy(policy => policy.Duration = TimeSpan.FromMinutes(10));
        configure?.Invoke(options);

        store = new InMemoryOutputCacheStore(options.SizeLimit, options.TimeProvider);
        return new OutputCacheMiddleware(store, options);
    }

    private static async Task<string> RunAsync(OutputCacheMiddleware middleware, OutputCacheTestContext context, WebApplicationMiddleware downstream)
    {
        await middleware.InvokeAsync(context, downstream);
        context.Response.Body.Position = 0;
        using System.IO.StreamReader reader = new(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static WebApplicationMiddleware Handler(Counter counter, string body, Action<OutputCacheTestContext>? shape = null)
        => async context =>
        {
            counter.Count++;
            OutputCacheTestContext ctx = (OutputCacheTestContext)context;
            ctx.Response.StatusCode = HttpStatusCode.Ok;
            shape?.Invoke(ctx);
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(body));
        };

    private sealed class Counter
    {
        public int Count;
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Middleware: A second request is served from cache without invoking downstream")]
    public async Task Invoke_SecondRequest_ShouldServeFromCacheAndSkipDownstream()
    {
        // Arrange
        OutputCacheMiddleware middleware = CreateMiddleware(out _);
        Counter counter = new();

        // Act
        string first = await RunAsync(middleware, new OutputCacheTestContext(), Handler(counter, "payload"));
        OutputCacheTestContext secondContext = new();
        string second = await RunAsync(middleware, secondContext, Handler(counter, "SHOULD-NOT-RUN"));

        // Assert
        first.ShouldBe("payload");
        second.ShouldBe("payload");
        counter.Count.ShouldBe(1);
        secondContext.Response.Headers.ContainsKey(HttpHeaderKey.Age).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Middleware: An authenticated request bypasses the cache by default")]
    public async Task Invoke_AuthorizationHeader_ShouldBypass()
    {
        // Arrange
        OutputCacheMiddleware middleware = CreateMiddleware(out _);
        Counter counter = new();

        static void Authorize(OutputCacheTestContext ctx) => ctx.Request.Headers[HttpHeaderKey.Authorization] = "Bearer token";

        // Act
        OutputCacheTestContext c1 = new();
        Authorize(c1);
        await RunAsync(middleware, c1, Handler(counter, "a"));
        OutputCacheTestContext c2 = new();
        Authorize(c2);
        await RunAsync(middleware, c2, Handler(counter, "b"));

        // Assert — never cached, so downstream runs each time.
        counter.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Middleware: A response with Set-Cookie is not stored")]
    public async Task Invoke_SetCookieResponse_ShouldNotStore()
    {
        // Arrange
        OutputCacheMiddleware middleware = CreateMiddleware(out _);
        Counter counter = new();
        WebApplicationMiddleware handler = Handler(counter, "x", ctx => ctx.Response.Headers[HttpHeaderKey.SetCookie] = "session=1");

        // Act
        await RunAsync(middleware, new OutputCacheTestContext(), handler);
        await RunAsync(middleware, new OutputCacheTestContext(), handler);

        // Assert
        counter.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Middleware: CacheAuthenticated stores the response but never replays its Set-Cookie")]
    public async Task Invoke_CacheAuthenticatedSetCookie_ShouldServeHitWithoutSetCookie()
    {
        // Arrange — the policy opts authenticated responses into shared storage; the cookie grant
        // itself is per-recipient and must never be part of the stored representation.
        OutputCacheMiddleware middleware = CreateMiddleware(
            out _,
            options => options.AddBasePolicy(policy =>
            {
                policy.Duration = TimeSpan.FromMinutes(10);
                policy.CacheAuthenticated = true;
            }));
        Counter counter = new();
        WebApplicationMiddleware handler = Handler(counter, "shared", ctx => ctx.Response.Headers[HttpHeaderKey.SetCookie] = "session=1");

        // Act
        string first = await RunAsync(middleware, new OutputCacheTestContext(), handler);
        OutputCacheTestContext hitContext = new();
        string second = await RunAsync(middleware, hitContext, Handler(counter, "SHOULD-NOT-RUN"));

        // Assert — the body is shared from the store, the cookie grant is not.
        first.ShouldBe("shared");
        second.ShouldBe("shared");
        counter.Count.ShouldBe(1);
        hitContext.Response.Headers.ContainsKey(HttpHeaderKey.SetCookie).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Middleware: A request no-store directive bypasses the cache")]
    public async Task Invoke_RequestNoStore_ShouldBypass()
    {
        // Arrange
        OutputCacheMiddleware middleware = CreateMiddleware(out _);
        Counter counter = new();

        static void NoStore(OutputCacheTestContext ctx) => ctx.Request.Headers[HttpHeaderKey.CacheControl] = "no-store";

        // Act
        OutputCacheTestContext c1 = new();
        NoStore(c1);
        await RunAsync(middleware, c1, Handler(counter, "a"));
        OutputCacheTestContext c2 = new();
        NoStore(c2);
        await RunAsync(middleware, c2, Handler(counter, "b"));

        // Assert
        counter.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Middleware: A non-200 response is not stored")]
    public async Task Invoke_NonOkStatus_ShouldNotStore()
    {
        // Arrange
        OutputCacheMiddleware middleware = CreateMiddleware(out _);
        Counter counter = new();
        WebApplicationMiddleware handler = async context =>
        {
            counter.Count++;
            context.Response.StatusCode = HttpStatusCode.InternalServerError;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("err"));
        };

        // Act
        await RunAsync(middleware, new OutputCacheTestContext(), handler);
        await RunAsync(middleware, new OutputCacheTestContext(), handler);

        // Assert
        counter.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Middleware: A response above the per-entry cap is streamed but not cached")]
    public async Task Invoke_BodyOverCap_ShouldStreamButNotCache()
    {
        // Arrange — a 16-byte cap cannot hold a 64-byte body.
        OutputCacheMiddleware middleware = CreateMiddleware(out _, options => options.MaximumBodySize = 16);
        Counter counter = new();
        string large = new('z', 64);

        // Act
        string first = await RunAsync(middleware, new OutputCacheTestContext(), Handler(counter, large));
        string second = await RunAsync(middleware, new OutputCacheTestContext(), Handler(counter, large));

        // Assert — the client is served both times (stream-through), but nothing was cached.
        first.ShouldBe(large);
        second.ShouldBe(large);
        counter.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Middleware: A non-cacheable method is not cached")]
    public async Task Invoke_PostRequest_ShouldBypass()
    {
        // Arrange
        OutputCacheMiddleware middleware = CreateMiddleware(out _);
        Counter counter = new();

        static void Post(OutputCacheTestContext ctx) => ctx.Request.Method = HttpMethod.Post;

        // Act
        OutputCacheTestContext c1 = new();
        Post(c1);
        await RunAsync(middleware, c1, Handler(counter, "a"));
        OutputCacheTestContext c2 = new();
        Post(c2);
        await RunAsync(middleware, c2, Handler(counter, "b"));

        // Assert
        counter.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Middleware: A WebSocket handshake is never answered from the cache")]
    public async Task Invoke_WebSocketHandshake_ShouldNotBeServedFromCache()
    {
        // Arrange — a plain GET stored the URL's representation.
        OutputCacheMiddleware middleware = CreateMiddleware(out _);
        Counter counter = new();
        await RunAsync(middleware, new OutputCacheTestContext(), Handler(counter, "plain"));

        // Act — the HTTP/1.1 handshake for the same URL (RFC 6455 §4.1).
        OutputCacheTestContext handshake = new();
        WebSocketHandshake(handshake);
        await RunAsync(middleware, handshake, Handler(counter, "handshake"));

        // Assert — the endpoint answered it, not the cache.
        counter.Count.ShouldBe(2);
        handshake.Response.Headers.ContainsKey(HttpHeaderKey.Age).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Middleware: An exchange taken over by a protocol switch is not stored")]
    public async Task Invoke_TakenOverHandshake_ShouldNotStore()
    {
        // Arrange — an endpoint that accepts the switch writes the 101 through the takeover, so the
        // exchange's own response stays the untouched default: a 200 with no body.
        OutputCacheMiddleware middleware = CreateMiddleware(out _);
        Counter counter = new();
        WebApplicationMiddleware acceptsTheSwitch = context =>
        {
            counter.Count++;
            return Task.CompletedTask;
        };

        // Act
        OutputCacheTestContext handshake = new();
        WebSocketHandshake(handshake);
        await RunAsync(middleware, handshake, acceptsTheSwitch);
        OutputCacheTestContext plain = new();
        string body = await RunAsync(middleware, plain, Handler(counter, "plain"));

        // Assert — the next request for the URL reached the endpoint instead of an empty stored 200.
        body.ShouldBe("plain");
        counter.Count.ShouldBe(2);
        plain.Response.Headers.ContainsKey(HttpHeaderKey.Age).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Middleware: An extended CONNECT is neither served from nor stored to the cache")]
    public async Task Invoke_ExtendedConnect_ShouldBypass()
    {
        // Arrange — the HTTP/2 and HTTP/3 WebSocket handshake (RFC 8441, RFC 9220) is a CONNECT.
        OutputCacheMiddleware middleware = CreateMiddleware(out _);
        Counter counter = new();

        static void ExtendedConnect(OutputCacheTestContext ctx)
        {
            ctx.Request.Method = HttpMethod.Connect;
            ctx.Request.Headers[HttpHeaderKey.SecWebSocketVersion] = "13";
        }

        // Act
        OutputCacheTestContext c1 = new();
        ExtendedConnect(c1);
        await RunAsync(middleware, c1, Handler(counter, "a"));
        OutputCacheTestContext c2 = new();
        ExtendedConnect(c2);
        await RunAsync(middleware, c2, Handler(counter, "b"));

        // Assert
        counter.Count.ShouldBe(2);
        c2.Response.Headers.ContainsKey(HttpHeaderKey.Age).ShouldBeFalse();
    }

    private static void WebSocketHandshake(OutputCacheTestContext context)
    {
        context.Request.Headers[HttpHeaderKey.Connection] = "Upgrade";
        context.Request.Headers[HttpHeaderKey.Upgrade] = "websocket";
        context.Request.Headers[HttpHeaderKey.SecWebSocketKey] = "dGhlIHNhbXBsZSBub25jZQ==";
        context.Request.Headers[HttpHeaderKey.SecWebSocketVersion] = "13";
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Middleware: The published endpoint's metadata should decide without running the route matcher")]
    public async Task Invoke_PublishedEndpointMetadata_ShouldCacheWithoutMatchingAgain()
    {
        // Arrange — opt-in mode (no base policy), so only the published endpoint's metadata can enable
        // caching; the router is unreachable, so running the matcher a second time would throw.
        OutputCacheOptions options = new();
        using InMemoryOutputCacheStore store = new(options.SizeLimit, options.TimeProvider);
        OutputCacheMiddleware middleware = new(store, options);
        Counter counter = new();

        static OutputCacheTestContext Routed()
        {
            OutputCacheTestContext context = new();
            context.Features.Set<IRouterFeature>(new ThrowingRouterFeature());
            context.Features.Set<IRouteMatchFeature>(new FakeRouteMatchFeature(OutputCacheMetadata.Enabled));
            return context;
        }

        // Act
        string first = await RunAsync(middleware, Routed(), Handler(counter, "payload"));
        string second = await RunAsync(middleware, Routed(), Handler(counter, "SHOULD-NOT-RUN"));

        // Assert
        first.ShouldBe("payload");
        second.ShouldBe("payload");
        counter.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Middleware: Disabled metadata on the published endpoint should bypass the base policy")]
    public async Task Invoke_PublishedEndpointDisabled_ShouldBypassBasePolicy()
    {
        // Arrange
        OutputCacheMiddleware middleware = CreateMiddleware(out _);
        Counter counter = new();

        static OutputCacheTestContext Routed()
        {
            OutputCacheTestContext context = new();
            context.Features.Set<IRouteMatchFeature>(new FakeRouteMatchFeature(OutputCacheMetadata.Disabled));
            return context;
        }

        // Act
        await RunAsync(middleware, Routed(), Handler(counter, "a"));
        await RunAsync(middleware, Routed(), Handler(counter, "b"));

        // Assert — never cached, so the endpoint runs each time.
        counter.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Middleware: The published endpoint's route values should partition VaryByRouteValue")]
    public async Task Invoke_PublishedRouteValues_ShouldPartitionVaryByRouteValue()
    {
        // Arrange — one path, and a route value routing resolved differently for each request.
        OutputCacheMiddleware middleware = CreateMiddleware(out _, options => options.AddBasePolicy(policy => policy.VaryByRouteValue("tenant")));
        Counter counter = new();

        static OutputCacheTestContext Routed(string tenant)
        {
            OutputCacheTestContext context = new();
            context.Features.Set<IRouteMatchFeature>(new FakeRouteMatchFeature(new RouteValueDictionary { ["tenant"] = tenant }));
            return context;
        }

        // Act
        string alpha = await RunAsync(middleware, Routed("alpha"), Handler(counter, "alpha-1"));
        string beta = await RunAsync(middleware, Routed("beta"), Handler(counter, "beta-1"));
        string alphaAgain = await RunAsync(middleware, Routed("alpha"), Handler(counter, "SHOULD-NOT-RUN"));

        // Assert
        alpha.ShouldBe("alpha-1");
        beta.ShouldBe("beta-1");
        alphaAgain.ShouldBe("alpha-1");
        counter.Count.ShouldBe(2);
    }
}
