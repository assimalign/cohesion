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

namespace Assimalign.Cohesion.Web.RequestTimeouts.Tests;

/// <summary>
/// Full-pipeline coverage over the <see cref="WebApplicationTestFactory"/> (in-memory HTTP/1.1):
/// a slow handler is answered with 504 on the wire, per-endpoint metadata overrides the global
/// default in both directions, and a disabled endpoint runs past the global timeout.
/// </summary>
public class RequestTimeoutEndToEndTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GlobalTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan NeverInTestBudget = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan PastGlobalTimeout = TimeSpan.FromMilliseconds(600);

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - E2E: A slow handler should be answered with 504 on the wire")]
    public async Task UseRequestTimeouts_SlowHandler_ShouldAnswer504()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        factory.Application.UseRequestTimeouts(GlobalTimeout);
        factory.Application.Use(async (context, next) =>
        {
            await Task.Delay(NeverInTestBudget, context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/slow", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.GatewayTimeout);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - E2E: A problem-details policy should answer application/problem+json")]
    public async Task UseRequestTimeouts_ProblemDetailsPolicy_ShouldAnswerProblemJson()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        factory.Application.UseRequestTimeouts(options => options.DefaultPolicy = new RequestTimeoutPolicy
        {
            Timeout = GlobalTimeout,
            WriteProblemDetails = true,
        });
        factory.Application.Use(async (context, next) =>
        {
            await Task.Delay(NeverInTestBudget, context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/slow", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.GatewayTimeout);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        body.ShouldContain("\"status\":504", Case.Sensitive);
        body.ShouldContain("Gateway Timeout", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - E2E: An endpoint policy shorter than the global default should win")]
    public async Task UseRequestTimeouts_EndpointShorterThanGlobal_ShouldWinPrecedence()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        factory.Application.UseRequestTimeouts(NeverInTestBudget);

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(new Route(
            CohesionHttpMethod.Get,
            "/slow",
            new RouterRouteHandler(async context =>
            {
                await Task.Delay(NeverInTestBudget, context.RequestCancelled);
            }),
            new RouterRouteMetadataCollection(new RequestTimeoutMetadata(GlobalTimeout))));

        using HttpClient client = factory.CreateClient();

        // Act — only the 200ms endpoint policy can produce a response inside the test budget.
        using HttpResponseMessage response = await client.GetAsync("/slow", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.GatewayTimeout);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - E2E: An endpoint policy longer than the global default should extend past it")]
    public async Task UseRequestTimeouts_EndpointLongerThanGlobal_ShouldExtendPastGlobal()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        factory.Application.UseRequestTimeouts(GlobalTimeout);

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(new Route(
            CohesionHttpMethod.Get,
            "/steady",
            new RouterRouteHandler(async context =>
            {
                await Task.Delay(PastGlobalTimeout, context.RequestCancelled);
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("finished"), context.RequestCancelled);
            }),
            new RouterRouteMetadataCollection(new RequestTimeoutMetadata(NeverInTestBudget))));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/steady", cancellationToken);

        // Assert — the handler ran three times the global timeout and still completed.
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("finished");
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - E2E: A disabled endpoint should run past the global timeout")]
    public async Task UseRequestTimeouts_EndpointDisabled_ShouldServePastGlobalTimeout()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddRouting();

        factory.Application.UseRequestTimeouts(GlobalTimeout);

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(new Route(
            CohesionHttpMethod.Get,
            "/unhurried",
            new RouterRouteHandler(async context =>
            {
                await Task.Delay(PastGlobalTimeout, context.RequestCancelled);
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("unhurried"), context.RequestCancelled);
            }),
            new RouterRouteMetadataCollection(RequestTimeoutMetadata.Disabled)));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/unhurried", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("unhurried");
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - E2E: A client-cancelled request should surface as cancellation, not a timeout response")]
    public async Task UseRequestTimeouts_ClientCancelsRequest_ShouldSurfaceAsCancellation()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        factory.Application.UseRequestTimeouts(TimeSpan.FromSeconds(2));
        factory.Application.Use(async (context, next) =>
        {
            await Task.Delay(NeverInTestBudget, context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();
        using CancellationTokenSource clientCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        clientCancellation.CancelAfter(TimeSpan.FromMilliseconds(150));

        // Act / Assert — the client observes its own cancellation; no 504 arrives.
        await Should.ThrowAsync<TaskCanceledException>(client.GetAsync("/abandoned", clientCancellation.Token));
    }
}
