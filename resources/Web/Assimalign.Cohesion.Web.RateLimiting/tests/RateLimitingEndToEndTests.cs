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
using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.RateLimiting.Tests;

/// <summary>
/// Full-pipeline coverage over the <see cref="WebApplicationTestFactory"/> (in-memory HTTP/1.1), with the
/// policies registered through the generated <c>builder.Services.AddRateLimiting</c> verb and placed by
/// <c>UseRateLimiting()</c>: the global limiter answers a second same-window request with 429 +
/// Retry-After on the wire, a per-endpoint policy gates its route through the real router when placed
/// after <c>UseRouting</c>, a CORS preflight leaves the policy untouched, the OnRejected hook shapes the
/// wire response, an unregistered middleware passes everything through, the last of two registrations
/// wins, and a middleware placed ahead of <c>UseRouting</c> keeps its global limiter but fails an
/// endpoint whose policy it could not apply. Requests are sequential on one client — safe for the window
/// limiter (its permit is not returned on completion) and for the sequential in-memory dispatch.
/// </summary>
public class RateLimitingEndToEndTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _longWindow = TimeSpan.FromHours(1);

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - E2E: An exhausted global limiter should answer 429 on the wire")]
    public async Task UseRateLimiting_GlobalLimiterExhausted_ShouldAnswer429()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRateLimiting(rateLimiting => rateLimiting.Options.GlobalPolicy = FixedWindowSingle());

        factory.Application.UseRateLimiting();
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
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRateLimiting(rateLimiting => rateLimiting.Options.GlobalPolicy = FixedWindowSingle());

        factory.Application.UseRateLimiting();
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
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Builder.Services.AddRateLimiting(rateLimiting => rateLimiting.Options.AddPolicy("expensive", FixedWindowSingle("expensive")));

        int endpointInvocations = 0;

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(new Route(
            CohesionHttpMethod.Get,
            "/expensive",
            new RouterRouteHandler(context =>
            {
                Interlocked.Increment(ref endpointInvocations);
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                return Task.CompletedTask;
            }),
            new RouterRouteMetadataCollection(new RateLimitingMetadata("expensive"))));

        factory.Application.UseRateLimiting();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage first = await client.GetAsync("/expensive", cancellationToken);
        using HttpResponseMessage second = await client.GetAsync("/expensive", cancellationToken);

        // Assert
        first.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        second.StatusCode.ShouldBe(NetHttpStatusCode.TooManyRequests);
        endpointInvocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - E2E: A CORS preflight should not consume its candidate endpoint's permit")]
    public async Task UseRateLimiting_CorsPreflight_ShouldNotConsumeEndpointPermit()
    {
        // Arrange — the DELETE route allows one request per window; nothing answers the preflight.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Builder.Services.AddRateLimiting(rateLimiting => rateLimiting.Options.AddPolicy("writes", FixedWindowSingle("writes")));

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(new Route(
            CohesionHttpMethod.Delete,
            "/items/{id:int}",
            new RouterRouteHandler(context =>
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                return Task.CompletedTask;
            }),
            new RouterRouteMetadataCollection(new RateLimitingMetadata("writes"))));

        factory.Application.UseRateLimiting();

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage preflight = new(NetHttpMethod.Options, "/items/7");
        preflight.Headers.TryAddWithoutValidation("Origin", "https://app.example").ShouldBeTrue();
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "DELETE").ShouldBeTrue();

        // Act
        using HttpResponseMessage preflightResponse = await client.SendAsync(preflight, cancellationToken);
        using HttpResponseMessage first = await client.DeleteAsync("/items/7", cancellationToken);
        using HttpResponseMessage second = await client.DeleteAsync("/items/7", cancellationToken);

        // Assert — the unanswered preflight is the plain OPTIONS request it is; the window's one permit
        // is still there for the actual request.
        preflightResponse.StatusCode.ShouldBe(NetHttpStatusCode.MethodNotAllowed);
        first.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        second.StatusCode.ShouldBe(NetHttpStatusCode.TooManyRequests);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - E2E: Registered before UseRouting, the global limiter should still gate every request")]
    public async Task UseRateLimiting_RegisteredBeforeRouting_ShouldStillApplyGlobalLimiter()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Builder.Services.AddRateLimiting(rateLimiting => rateLimiting.Options.GlobalPolicy = FixedWindowSingle());

        factory.Application.UseRateLimiting();

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(new Route(
            CohesionHttpMethod.Get,
            "/open",
            new RouterRouteHandler(context =>
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                return Task.CompletedTask;
            })));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage first = await client.GetAsync("/open", cancellationToken);
        using HttpResponseMessage second = await client.GetAsync("/open", cancellationToken);

        // Assert
        first.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        second.StatusCode.ShouldBe(NetHttpStatusCode.TooManyRequests);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - E2E: Registered before UseRouting, an endpoint policy should fail the request at dispatch instead of running unlimited")]
    public async Task UseRateLimiting_RegisteredBeforeRouting_ShouldFailEndpointWithPolicy()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Builder.Services.AddRateLimiting(rateLimiting => rateLimiting.Options.AddPolicy("expensive", FixedWindowSingle("expensive")));

        InvalidOperationException? dispatchFailure = null;
        int endpointInvocations = 0;

        // Observes the dispatch failure the way an exception boundary would.
        factory.Application.Use(async (context, next) =>
        {
            try
            {
                await next.Invoke(context);
            }
            catch (InvalidOperationException exception)
            {
                dispatchFailure = exception;
                context.Response.StatusCode = CohesionHttpStatusCode.InternalServerError;
            }
        });

        factory.Application.UseRateLimiting();

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(new Route(
            CohesionHttpMethod.Get,
            "/expensive",
            new RouterRouteHandler(context =>
            {
                Interlocked.Increment(ref endpointInvocations);
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                return Task.CompletedTask;
            }),
            new RouterRouteMetadataCollection(new RateLimitingMetadata("expensive"))));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/expensive", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        endpointInvocations.ShouldBe(0);
        dispatchFailure.ShouldNotBeNull().Message.ShouldContain("UseRateLimiting()", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - E2E: The OnRejected hook should shape the wire response")]
    public async Task UseRateLimiting_OnRejected_ShouldShapeWireResponse()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRateLimiting(rateLimiting =>
        {
            rateLimiting.Options.GlobalPolicy = FixedWindowSingle();
            rateLimiting.Options.OnRejected = async (rejection, token) =>
            {
                await rejection.Context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("throttled"), token);
            };
        });

        factory.Application.UseRateLimiting();
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
        using CancellationTokenSource cancellation = new(_testTimeout);
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

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - E2E: The last AddRateLimiting registration should be the one UseRateLimiting places")]
    public async Task AddRateLimiting_RegisteredTwice_ShouldApplyTheLastRegistration()
    {
        // Arrange — both registrations exhaust after one request; the first rejects with 503, the last
        // with the default 429.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRateLimiting(rateLimiting =>
        {
            rateLimiting.Options.GlobalPolicy = FixedWindowSingle("first");
            rateLimiting.Options.RejectionStatusCode = CohesionHttpStatusCode.ServiceUnavailable;
        });
        factory.Builder.Services.AddRateLimiting(rateLimiting => rateLimiting.Options.GlobalPolicy = FixedWindowSingle("last"));

        factory.Application.UseRateLimiting();
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

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - E2E: AddRateLimiting with a service-provider callback should configure the middleware UseRateLimiting places")]
    public async Task AddRateLimiting_ServiceProviderCallback_ShouldConfigureThePlacedMiddleware()
    {
        // Arrange — the provider overload builds on first resolve, when the pipeline is composed.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        bool configured = false;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRateLimiting((services, rateLimiting) =>
        {
            configured = services is not null;
            rateLimiting.Options.GlobalPolicy = FixedWindowSingle();
        });

        factory.Application.UseRateLimiting();
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
        configured.ShouldBeTrue();
        first.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        second.StatusCode.ShouldBe(NetHttpStatusCode.TooManyRequests);
    }

    private static RateLimitingPolicy FixedWindowSingle(string key = "test")
        => RateLimitingPolicy.Create(_ => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            key,
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 1,
                Window = _longWindow,
                QueueLimit = 0,
            }));
}
