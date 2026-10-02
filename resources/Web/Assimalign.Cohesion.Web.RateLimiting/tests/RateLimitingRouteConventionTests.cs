using System;
using System.Net.Http;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.RateLimiting.Tests;

/// <summary>
/// The endpoint convention verbs (#1055) over the real router: <c>RequireRateLimiting</c> on a mapped
/// route and on a route group, and <c>DisableRateLimiting</c> exempting one route of a limited group.
/// </summary>
public class RateLimitingRouteConventionTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _longWindow = TimeSpan.FromHours(1);

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - Conventions: A named policy required on a route should gate only that route")]
    public async Task RequireRateLimiting_NamedPolicyOnRoute_ShouldGateOnlyThatRoute()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(CohesionHttpMethod.Get, "/expensive", Ok()).RequireRateLimiting("expensive");
        routes.Map(CohesionHttpMethod.Get, "/cheap", Ok());

        factory.Application.UseRateLimiting(options => options.AddPolicy("expensive", SinglePermitPerPath()));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage expensive1 = await client.GetAsync("/expensive", cancellationToken);
        using HttpResponseMessage expensive2 = await client.GetAsync("/expensive", cancellationToken);
        using HttpResponseMessage cheap1 = await client.GetAsync("/cheap", cancellationToken);
        using HttpResponseMessage cheap2 = await client.GetAsync("/cheap", cancellationToken);

        // Assert
        expensive1.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        expensive2.StatusCode.ShouldBe(NetHttpStatusCode.TooManyRequests);
        cheap1.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        cheap2.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - Conventions: A group policy should gate routes mapped before and after it")]
    public async Task RequireRateLimiting_InlinePolicyOnGroup_ShouldGateRoutesMappedBeforeAndAfter()
    {
        // Arrange — the policy is attached between the two Map calls; composition happens at build.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        IRouterGroupBuilder api = routes.MapGroup("/api");
        api.Map(CohesionHttpMethod.Get, "early", Ok());
        api.RequireRateLimiting(SinglePermitPerPath());
        api.Map(CohesionHttpMethod.Get, "late", Ok());

        factory.Application.UseRateLimiting();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage early1 = await client.GetAsync("/api/early", cancellationToken);
        using HttpResponseMessage early2 = await client.GetAsync("/api/early", cancellationToken);
        using HttpResponseMessage late1 = await client.GetAsync("/api/late", cancellationToken);
        using HttpResponseMessage late2 = await client.GetAsync("/api/late", cancellationToken);

        // Assert
        early1.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        early2.StatusCode.ShouldBe(NetHttpStatusCode.TooManyRequests);
        late1.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        late2.StatusCode.ShouldBe(NetHttpStatusCode.TooManyRequests);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - Conventions: Disabling rate limiting on a route should exempt it from its group's policy")]
    public async Task DisableRateLimiting_OnRouteInLimitedGroup_ShouldExemptTheRoute()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        IRouterGroupBuilder api = routes.MapGroup("/api").RequireRateLimiting(SinglePermitPerPath());
        api.Map(CohesionHttpMethod.Get, "open", Ok()).DisableRateLimiting();

        factory.Application.UseRateLimiting();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage first = await client.GetAsync("/api/open", cancellationToken);
        using HttpResponseMessage second = await client.GetAsync("/api/open", cancellationToken);

        // Assert
        first.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        second.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - Conventions: A route that disables its group's policy should run without UseRateLimiting")]
    public async Task DisableRateLimiting_OnRouteWithoutMiddleware_ShouldRunTheRoute()
    {
        // Arrange — no UseRateLimiting at all: the group's route still fails closed, the exempt one runs.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        InvalidOperationException? dispatchFailure = null;

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

        IRouterBuilder routes = factory.Application.UseRouting();
        IRouterGroupBuilder api = routes.MapGroup("/api").RequireRateLimiting(SinglePermitPerPath());
        api.Map(CohesionHttpMethod.Get, "limited", Ok());
        api.Map(CohesionHttpMethod.Get, "open", Ok()).DisableRateLimiting();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage open = await client.GetAsync("/api/open", cancellationToken);
        InvalidOperationException? openFailure = dispatchFailure;
        using HttpResponseMessage limited = await client.GetAsync("/api/limited", cancellationToken);

        // Assert
        open.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        openFailure.ShouldBeNull();
        limited.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        dispatchFailure.ShouldNotBeNull().Message.ShouldContain("UseRateLimiting()", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RateLimiting] - Conventions: A verb on a null builder should throw ArgumentNullException")]
    public void RequireRateLimiting_NullBuilder_ShouldThrow()
    {
        // Arrange
        IRouterRouteBuilder builder = null!;

        // Act
        Action act = () => builder.RequireRateLimiting("policy");

        // Assert
        act.ShouldThrow<ArgumentNullException>();
    }

    private static RouterRouteHandler Ok() => new(context =>
    {
        context.Response.StatusCode = CohesionHttpStatusCode.Ok;
        return Task.CompletedTask;
    });

    private static RateLimitingPolicy SinglePermitPerPath()
        => RateLimitingPolicy.Create(context => RateLimitPartition.GetFixedWindowLimiter(
            context.Request.Path.Value,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 1,
                Window = _longWindow,
                QueueLimit = 0,
            }));
}
