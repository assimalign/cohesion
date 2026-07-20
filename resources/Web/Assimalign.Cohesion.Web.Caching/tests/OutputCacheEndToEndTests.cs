using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Routing.Metadata;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Caching.Tests;

/// <summary>
/// Full-pipeline coverage over the <see cref="WebApplicationTestFactory"/> (in-memory HTTP/1.1): a cache
/// hit skips downstream, a differing query misses, the response's own <c>Vary</c> header keeps a client
/// from receiving a variant it did not request, an authenticated request bypasses, tag eviction forces a
/// re-fetch, and per-endpoint opt-in through routing metadata caches only the marked endpoint. Requests
/// are sequential on one client, matching the in-memory transport's sequential dispatch.
/// </summary>
public class OutputCacheEndToEndTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongDuration = TimeSpan.FromMinutes(30);

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - E2E: A second request is served from cache without running the endpoint")]
    public async Task UseOutputCache_SecondRequest_ShouldServeFromCache()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        int invocations = 0;

        factory.Application.UseOutputCache(options => options.AddBasePolicy(policy => policy.Duration = LongDuration));
        factory.Application.Use(async (context, next) =>
        {
            int n = Interlocked.Increment(ref invocations);
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"payload-{n}"), context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();

        // Act
        string first = await client.GetStringAsync("/cache-me", cancellationToken);
        string second = await client.GetStringAsync("/cache-me", cancellationToken);

        // Assert — the second body is the first response replayed, and the endpoint ran only once.
        first.ShouldBe("payload-1");
        second.ShouldBe("payload-1");
        invocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - E2E: A hit carries an Age header")]
    public async Task UseOutputCache_Hit_ShouldCarryAgeHeader()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Application.UseOutputCache(options => options.AddBasePolicy(policy => policy.Duration = LongDuration));
        factory.Application.Use(async (context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("body"), context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage first = await client.GetAsync("/", cancellationToken);
        using HttpResponseMessage second = await client.GetAsync("/", cancellationToken);

        // Assert
        second.Headers.Contains("Age").ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - E2E: A differing query string misses and re-runs the endpoint")]
    public async Task UseOutputCache_DifferentQuery_ShouldMiss()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        int invocations = 0;

        factory.Application.UseOutputCache(options => options.AddBasePolicy(policy => policy.Duration = LongDuration));
        factory.Application.Use(async (context, next) =>
        {
            int n = Interlocked.Increment(ref invocations);
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"n-{n}"), context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();

        // Act
        string a1 = await client.GetStringAsync("/list?page=1", cancellationToken);
        string b1 = await client.GetStringAsync("/list?page=2", cancellationToken);
        string a2 = await client.GetStringAsync("/list?page=1", cancellationToken);

        // Assert — page=1 and page=2 are distinct entries; the repeat of page=1 is a hit.
        a1.ShouldBe("n-1");
        b1.ShouldBe("n-2");
        a2.ShouldBe("n-1");
        invocations.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - E2E: A response Vary keeps a client from receiving a foreign variant")]
    public async Task UseOutputCache_ResponseVary_ShouldNotServeForeignVariant()
    {
        // Arrange — the endpoint varies its body by X-Client and advertises Vary: X-Client. A client that
        // did not request a stored variant must never receive it (the compression/negotiation cross-client
        // safety property proven over a generic Vary header).
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        int invocations = 0;

        factory.Application.UseOutputCache(options => options.AddBasePolicy(policy => policy.Duration = LongDuration));
        factory.Application.Use(async (context, next) =>
        {
            Interlocked.Increment(ref invocations);
            string client = context.Request.Headers.TryGetValue(new HttpHeaderKey("X-Client"), out HttpHeaderValue value)
                ? value.Value
                : "none";
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            context.Response.Headers[HttpHeaderKey.Vary] = "X-Client";
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"variant:{client}"), context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();

        // Act
        string alpha1 = await SendAsync(client, "alpha", cancellationToken);
        string beta = await SendAsync(client, "beta", cancellationToken);
        string alpha2 = await SendAsync(client, "alpha", cancellationToken);

        // Assert — beta never receives alpha's stored body (it misses and re-runs), while the alpha
        // repeat is a hit. The endpoint therefore ran exactly twice (alpha, beta).
        alpha1.ShouldBe("variant:alpha");
        beta.ShouldBe("variant:beta");
        alpha2.ShouldBe("variant:alpha");
        invocations.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - E2E: An authenticated request bypasses the cache")]
    public async Task UseOutputCache_AuthenticatedRequest_ShouldBypass()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        int invocations = 0;

        factory.Application.UseOutputCache(options => options.AddBasePolicy(policy => policy.Duration = LongDuration));
        factory.Application.Use(async (context, next) =>
        {
            Interlocked.Increment(ref invocations);
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("secret"), context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();

        // Act
        for (int i = 0; i < 2; i++)
        {
            using HttpRequestMessage request = new(NetHttpMethod.Get, "/account");
            request.Headers.Add("Authorization", "Bearer token");
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        }

        // Assert — an authenticated response is never cached, so the endpoint runs every time.
        invocations.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - E2E: Tag eviction forces a re-fetch")]
    public async Task UseOutputCache_EvictByTag_ShouldReFetch()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        using InMemoryOutputCacheStore store = new();
        int invocations = 0;

        factory.Application.UseOutputCache(store, options => options.AddBasePolicy(policy =>
        {
            policy.Duration = LongDuration;
            policy.Tag("catalog");
        }));
        factory.Application.Use(async (context, next) =>
        {
            int n = Interlocked.Increment(ref invocations);
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"v-{n}"), context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();

        // Act
        string first = await client.GetStringAsync("/catalog", cancellationToken);
        string cached = await client.GetStringAsync("/catalog", cancellationToken);
        await store.EvictByTagAsync("catalog", cancellationToken);
        string afterEvict = await client.GetStringAsync("/catalog", cancellationToken);

        // Assert
        first.ShouldBe("v-1");
        cached.ShouldBe("v-1");
        afterEvict.ShouldBe("v-2");
        invocations.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - E2E: Per-endpoint metadata caches only the opted-in endpoint")]
    public async Task UseOutputCache_PerEndpointOptIn_ShouldCacheOnlyMarkedEndpoint()
    {
        // Arrange — opt-in mode (no base policy): only the endpoint carrying OutputCacheMetadata.Enabled
        // is cached; the plain endpoint runs every time.
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        int cachedHits = 0;
        int plainHits = 0;

        factory.Application.UseOutputCache();

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(new Route(
            CohesionHttpMethod.Get,
            "/cached",
            new RouterRouteHandler(async context =>
            {
                int n = Interlocked.Increment(ref cachedHits);
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"c-{n}"), context.RequestCancelled);
            }),
            new RouterRouteMetadataCollection(OutputCacheMetadata.Enabled)));
        routes.Map(new Route(
            CohesionHttpMethod.Get,
            "/plain",
            new RouterRouteHandler(async context =>
            {
                int n = Interlocked.Increment(ref plainHits);
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"p-{n}"), context.RequestCancelled);
            })));

        using HttpClient client = factory.CreateClient();

        // Act
        string cached1 = await client.GetStringAsync("/cached", cancellationToken);
        string cached2 = await client.GetStringAsync("/cached", cancellationToken);
        string plain1 = await client.GetStringAsync("/plain", cancellationToken);
        string plain2 = await client.GetStringAsync("/plain", cancellationToken);

        // Assert
        cached1.ShouldBe("c-1");
        cached2.ShouldBe("c-1");
        cachedHits.ShouldBe(1);

        plain1.ShouldBe("p-1");
        plain2.ShouldBe("p-2");
        plainHits.ShouldBe(2);
    }

    private static async Task<string> SendAsync(HttpClient client, string clientTag, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(NetHttpMethod.Get, "/vary");
        request.Headers.Add("X-Client", clientTag);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
