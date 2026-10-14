using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Caching.Internal;
using Assimalign.Cohesion.Web.Caching.Tests.TestObjects;
using Assimalign.Cohesion.Web.ForwardedHeaders;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpMethod = System.Net.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Caching.Tests;

/// <summary>
/// Proxy-awareness coverage (#1050): the primary cache key is built from the effective scheme and host.
/// End-to-end cases put the real forwarded-headers middleware in front of <c>UseOutputCache</c> over
/// <see cref="WebApplicationTestFactory"/>, whose in-memory peer is trusted as a local transport; the
/// unit case runs the same middleware over <see cref="OutputCacheTestContext"/> with plaintext requests
/// from an explicit proxy address.
/// </summary>
public class OutputCacheForwardedTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _longDuration = TimeSpan.FromMinutes(30);
    private static readonly IPEndPoint _trustedProxy = new(IPAddress.Parse("10.0.0.2"), 51000);

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Forwarded: Each forwarded host should get its own cache entry")]
    public async Task UseOutputCache_ForwardedHosts_ShouldPartitionEntries()
    {
        // Arrange — the proxy rewrites Host to its upstream name; the client-facing host is forwarded.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        int invocations = 0;
        ComposeCountingApplication(factory, useForwardedHeaders: true, () => Interlocked.Increment(ref invocations));
        using HttpClient client = factory.CreateClient();

        // Act
        string tenantA = await SendAsync(client, "https", "tenant-a.example", cancellation.Token);
        string tenantB = await SendAsync(client, "https", "tenant-b.example", cancellation.Token);
        string tenantAAgain = await SendAsync(client, "https", "tenant-a.example", cancellation.Token);

        // Assert — tenant B never receives tenant A's stored response, and a repeat still hits.
        tenantA.ShouldBe("payload-1");
        tenantB.ShouldBe("payload-2");
        tenantAAgain.ShouldBe("payload-1");
        invocations.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Forwarded: Each forwarded scheme should get its own cache entry")]
    public async Task UseOutputCache_ForwardedSchemes_ShouldPartitionEntries()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        int invocations = 0;
        ComposeCountingApplication(factory, useForwardedHeaders: true, () => Interlocked.Increment(ref invocations));
        using HttpClient client = factory.CreateClient();

        // Act — the wire scheme is http for all three; only the proxy-asserted scheme differs.
        string secure = await SendAsync(client, "https", "public.example", cancellation.Token);
        string plaintext = await SendAsync(client, "http", "public.example", cancellation.Token);
        string secureAgain = await SendAsync(client, "https", "public.example", cancellation.Token);

        // Assert
        secure.ShouldBe("payload-1");
        plaintext.ShouldBe("payload-2");
        secureAgain.ShouldBe("payload-1");
        invocations.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Forwarded: Without UseForwardedHeaders a spoofed X-Forwarded-Host should not partition the cache")]
    public async Task UseOutputCache_WithoutForwardedHeaders_ShouldKeyOnWireHost()
    {
        // Arrange — no forwarded-headers middleware: the key uses the wire host for every request.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();
        int invocations = 0;
        ComposeCountingApplication(factory, useForwardedHeaders: false, () => Interlocked.Increment(ref invocations));
        using HttpClient client = factory.CreateClient();

        // Act
        string first = await SendAsync(client, "https", "tenant-a.example", cancellation.Token);
        string second = await SendAsync(client, "https", "tenant-b.example", cancellation.Token);

        // Assert
        first.ShouldBe("payload-1");
        second.ShouldBe("payload-1");
        invocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Forwarded: Keys for requests from a trusted proxy address should follow the forwarded host")]
    public async Task BuildPrimaryKey_TrustedProxyAddress_ShouldFollowForwardedHost()
    {
        // Arrange — two upstream authorities forwarding the same public host, and a third request
        // forwarding a different one, all plaintext from the trusted proxy's address.
        OutputCachePolicy policy = new();
        OutputCacheTestContext first = await ResolveBehindProxyAsync("backend-1.internal:8080", "public.example");
        OutputCacheTestContext second = await ResolveBehindProxyAsync("backend-2.internal:8080", "public.example");
        OutputCacheTestContext other = await ResolveBehindProxyAsync("backend-1.internal:8080", "other.example");

        // Act
        string firstKey = OutputCacheKeyBuilder.BuildPrimaryKey(first, policy, routeValues: null);
        string secondKey = OutputCacheKeyBuilder.BuildPrimaryKey(second, policy, routeValues: null);
        string otherKey = OutputCacheKeyBuilder.BuildPrimaryKey(other, policy, routeValues: null);

        // Assert
        firstKey.ShouldBe(secondKey);
        firstKey.ShouldNotBe(otherKey);
    }

    private static async Task<OutputCacheTestContext> ResolveBehindProxyAsync(string wireHost, string forwardedHost)
    {
        OutputCacheTestContext context = new()
        {
            ConnectionInfo = new HttpConnectionInfo(remoteEndPoint: _trustedProxy),
        };
        context.Request.Host = new HttpHost(wireHost);
        context.Request.Path = new HttpPath("/catalog");
        context.Request.Headers[HttpHeaderKey.XForwardedFor] = "203.0.113.9";
        context.Request.Headers[HttpHeaderKey.XForwardedProto] = "https";
        context.Request.Headers[HttpHeaderKey.XForwardedHost] = forwardedHost;

        TestPipelineBuilder builder = new();
        builder.UseForwardedHeaders(options =>
        {
            options.Headers = ForwardedHeaderNames.XForwarded;
            options.KnownProxies.Add(_trustedProxy.Address);
        });
        IWebApplicationMiddleware forwarded = builder.LastMiddleware.ShouldNotBeNull();

        await forwarded.InvokeAsync(context, _ => Task.CompletedTask);

        return context;
    }

    private static void ComposeCountingApplication(WebApplicationTestFactory factory, bool useForwardedHeaders, Func<int> invoke)
    {
        if (useForwardedHeaders)
        {
            factory.Application.UseForwardedHeaders(options => options.Headers = ForwardedHeaderNames.XForwarded);
        }

        factory.Application.UseOutputCache(options => options.AddBasePolicy(policy => policy.Duration = _longDuration));
        factory.Application.Use(async (context, next) =>
        {
            int n = invoke();
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"payload-{n}"), context.RequestCancelled);
        });
    }

    private static async Task<string> SendAsync(HttpClient client, string forwardedProto, string forwardedHost, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(NetHttpMethod.Get, "http://internal.upstream/catalog");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.9").ShouldBeTrue();
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", forwardedProto).ShouldBeTrue();
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", forwardedHost).ShouldBeTrue();

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
