using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Routing.Metadata;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.RateLimiting.Tests;

/// <summary>
/// Full-pipeline coverage over the <see cref="WebApplicationTestFactory"/> (in-memory HTTP/1.1): the
/// global limiter answers a second same-window request with 429 + Retry-After on the wire, a per-endpoint
/// policy gates its matched route through the real router, the OnRejected hook shapes the wire response,
/// and an unconfigured middleware passes everything through. Requests are sequential on one client — safe
/// for the window limiter (its permit is not returned on completion) and for the sequential in-memory
/// dispatch.
/// </summary>
public class RateLimitingEndToEndTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongWindow = TimeSpan.FromHours(1);

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - E2E: An exhausted global limiter should answer 429 on the wire")]
    public async Task UseRateLimiting_GlobalLimiterExhausted_ShouldAnswer429()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        factory.Application.UseRateLimiting(options => options.GlobalPolicy = FixedWindowSingle());
        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage first = await client.GetAsync("/", cancellationToken);
        using HttpResponseMessage second = await client.GetAsync("/", cancellationToken);

        // Assert
        first.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        second.StatusCode.ShouldBe(NetHttpStatusCode.TooManyRequests);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - E2E: A rejection should carry a Retry-After header on the wire")]
    public async Task UseRateLimiting_Rejection_ShouldCarryRetryAfterHeader()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        factory.Application.UseRateLimiting(options => options.GlobalPolicy = FixedWindowSingle());
        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage first = await client.GetAsync("/", cancellationToken);
        using HttpResponseMessage second = await client.GetAsync("/", cancellationToken);

        // Assert
        second.StatusCode.ShouldBe(NetHttpStatusCode.TooManyRequests);
        second.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - E2E: A per-endpoint policy should gate its matched route")]
    public async Task UseRateLimiting_PerEndpointPolicy_ShouldGateMatchedRoute()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        factory.Application.UseRateLimiting(options => options.AddPolicy("expensive", FixedWindowSingle("expensive")));

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(new Route(
            CohesionHttpMethod.Get,
            "/expensive",
            new RouterRouteHandler(context =>
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                return Task.CompletedTask;
            }),
            new RouterRouteMetadataCollection(new RateLimitingMetadata("expensive"))));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage first = await client.GetAsync("/expensive", cancellationToken);
        using HttpResponseMessage second = await client.GetAsync("/expensive", cancellationToken);

        // Assert
        first.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        second.StatusCode.ShouldBe(NetHttpStatusCode.TooManyRequests);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - E2E: The OnRejected hook should shape the wire response")]
    public async Task UseRateLimiting_OnRejected_ShouldShapeWireResponse()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        factory.Application.UseRateLimiting(options =>
        {
            options.GlobalPolicy = FixedWindowSingle();
            options.OnRejected = async (rejection, token) =>
            {
                await rejection.Context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("throttled"), token);
            };
        });
        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage first = await client.GetAsync("/", cancellationToken);
        using HttpResponseMessage second = await client.GetAsync("/", cancellationToken);

        // Assert
        second.StatusCode.ShouldBe(NetHttpStatusCode.TooManyRequests);
        (await second.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("throttled");
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - E2E: With no policy configured every request should pass through")]
    public async Task UseRateLimiting_Unconfigured_ShouldPassThrough()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        factory.Application.UseRateLimiting();
        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();

        // Act / Assert
        for (int i = 0; i < 3; i++)
        {
            using HttpResponseMessage response = await client.GetAsync("/", cancellationToken);
            response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        }
    }

    private static RateLimitingPolicy FixedWindowSingle(string key = "test")
        => RateLimitingPolicy.Create(_ => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            key,
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 1,
                Window = LongWindow,
                QueueLimit = 0,
            }));
}
