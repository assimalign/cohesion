using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Caching.Tests;

/// <summary>Verifies storage uses successful response completion and the final callback-produced head.</summary>
public class OutputCacheStartingTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Theory(DisplayName = "Cohesion Test [Web.Caching] - Starting: Final callback restrictions prevent cache storage")]
    [InlineData("Cache-Control", "no-store", 200)]
    [InlineData("Cache-Control", "private", 200)]
    [InlineData("Cache-Control", "no-cache", 200)]
    [InlineData("Cache-Control", "max-age=0", 200)]
    [InlineData("Set-Cookie", "session=private", 200)]
    [InlineData("Vary", "*", 200)]
    [InlineData("X-Final", "error", 500)]
    public async Task UseOutputCache_StartingCallbackRestriction_ShouldNotStore(string name, string value, int status)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        await using WebApplicationTestFactory factory = new();
        int invocations = 0;
        factory.Application.Use(async (context, next) =>
        {
            context.Features.Get<IWebResponseStartingFeature>()!.OnStarting(_ =>
            {
                context.Response.Headers[new HttpHeaderKey(name)] = value;
                context.Response.StatusCode = new CohesionHttpStatusCode(status);
                return Task.CompletedTask;
            }, null);
            await next(context);
        });
        factory.Application.UseOutputCache(options => options.AddBasePolicy(_ => { }));
        factory.Application.Use(async (context, _) =>
        {
            int invocation = Interlocked.Increment(ref invocations);
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"payload-{invocation}"), context.RequestCancelled);
        });
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage first = await client.GetAsync("/", cancellation.Token);
        using HttpResponseMessage second = await client.GetAsync("/", cancellation.Token);

        // Assert
        ((int)first.StatusCode).ShouldBe(status);
        ((int)second.StatusCode).ShouldBe(status);
        (await first.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("payload-1");
        (await second.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("payload-2");
        second.Headers.Contains("Age").ShouldBeFalse();
        invocations.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Starting: Callback Vary partitions stored representations")]
    public async Task UseOutputCache_StartingCallbackVary_ShouldKeepVariantsSeparate()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        await using WebApplicationTestFactory factory = new();
        int invocations = 0;
        factory.Application.Use(async (context, next) =>
        {
            context.Features.Get<IWebResponseStartingFeature>()!.OnStarting(_ =>
            {
                context.Response.Headers[HttpHeaderKey.Vary] = "X-Client";
                return Task.CompletedTask;
            }, null);
            await next(context);
        });
        factory.Application.UseOutputCache(options => options.AddBasePolicy(_ => { }));
        factory.Application.Use(async (context, _) =>
        {
            Interlocked.Increment(ref invocations);
            string body = context.Request.Headers[new HttpHeaderKey("X-Client")].Value;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(body), context.RequestCancelled);
        });
        using HttpClient client = factory.CreateClient();

        async Task<string> SendAsync(string variant)
        {
            using HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, "/");
            request.Headers.Add("X-Client", variant);
            using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);
            return await response.Content.ReadAsStringAsync(cancellation.Token);
        }

        // Act
        string alpha = await SendAsync("alpha");
        string beta = await SendAsync("beta");
        string alphaHit = await SendAsync("alpha");

        // Assert
        alpha.ShouldBe("alpha");
        beta.ShouldBe("beta");
        alphaHit.ShouldBe("alpha");
        invocations.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Caching] - Starting: Stored headers include the endpoint callback's final values")]
    public async Task UseOutputCache_StartingCallbackHeader_ShouldReplayFinalHeader()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        await using WebApplicationTestFactory factory = new();
        int invocations = 0;
        factory.Application.UseOutputCache(options => options.AddBasePolicy(_ => { }));
        factory.Application.Use(async (context, _) =>
        {
            Interlocked.Increment(ref invocations);
            context.Response.Headers[HttpHeaderKey.ContentType] = "application/octet-stream";
            context.Features.Get<IWebResponseStartingFeature>()!.OnStarting(_ =>
            {
                context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain";
                return Task.CompletedTask;
            }, null);
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("payload"), context.RequestCancelled);
        });
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage first = await client.GetAsync("/", cancellation.Token);
        using HttpResponseMessage second = await client.GetAsync("/", cancellation.Token);

        // Assert
        first.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        second.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        second.Headers.Contains("Age").ShouldBeTrue();
        invocations.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Caching] - Starting: A failed callback never publishes the captured response")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UseOutputCache_StartingCallbackFailure_ShouldNotStore(bool http2)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions
        {
            Protocol = http2 ? WebApplicationTestProtocol.Http2 : WebApplicationTestProtocol.Http1,
        });
        int invocations = 0;
        factory.Application.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey(new HttpHeaderKey("X-Fail")))
            {
                context.Features.Get<IWebResponseStartingFeature>()!.OnStarting(_ =>
                    Task.FromException(new InvalidOperationException("Deliberate starting failure.")), null);
            }
            await next(context);
        });
        factory.Application.UseOutputCache(options => options.AddBasePolicy(_ => { }));
        factory.Application.Use(async (context, _) =>
        {
            int invocation = Interlocked.Increment(ref invocations);
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"payload-{invocation}"), context.RequestCancelled);
        });
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage failed = new(System.Net.Http.HttpMethod.Get, "/")
        {
            Version = client.DefaultRequestVersion,
            VersionPolicy = client.DefaultVersionPolicy,
            // A request body prevents HTTP/1.1's automatic retry of a refused connection.
            Content = new StringContent("request"),
        };
        failed.Headers.Add("X-Fail", "true");

        // Act
        await Should.ThrowAsync<HttpRequestException>(() => client.SendAsync(failed, cancellation.Token));
        string second = await client.GetStringAsync("/", cancellation.Token);

        // Assert
        second.ShouldBe("payload-2");
        invocations.ShouldBe(2);
    }
}
