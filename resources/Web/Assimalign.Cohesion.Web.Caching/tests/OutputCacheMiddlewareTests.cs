using System;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Caching.Internal;
using Assimalign.Cohesion.Web.Caching.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Caching.Tests;

/// <summary>
/// Unit coverage for the middleware's cache-or-bypass decisions driven directly over an in-memory
/// context double: the hit path skips downstream and stamps <c>Age</c>; the request/response bypass
/// matrix (authenticated request, <c>Set-Cookie</c> response, request <c>no-store</c>, non-200 status,
/// over-cap body) never serves a stale or shared representation.
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
}
