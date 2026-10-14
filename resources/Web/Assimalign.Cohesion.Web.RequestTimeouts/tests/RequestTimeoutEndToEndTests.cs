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
/// a slow handler is answered with 504 on the wire, per-endpoint metadata read from the endpoint
/// <c>UseRouting</c> published overrides the global default in both directions, a disabled endpoint
/// runs past the global timeout, and a middleware registered ahead of <c>UseRouting</c> fails an
/// endpoint whose timeout it could not apply while placing no requirement on a disabled one.
/// </summary>
public class RequestTimeoutEndToEndTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _globalTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan _neverInTestBudget = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan _pastGlobalTimeout = TimeSpan.FromMilliseconds(600);

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - E2E: A slow handler should be answered with 504 on the wire")]
    public async Task UseRequestTimeouts_SlowHandler_ShouldAnswer504()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        factory.Application.UseRequestTimeouts(_globalTimeout);
        factory.Application.Use(async (context, next) =>
        {
            await Task.Delay(_neverInTestBudget, context.RequestCancelled);
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
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        factory.Application.UseRequestTimeouts(options => options.DefaultPolicy = new RequestTimeoutPolicy
        {
            Timeout = _globalTimeout,
            WriteProblemDetails = true,
        });
        factory.Application.Use(async (context, next) =>
        {
            await Task.Delay(_neverInTestBudget, context.RequestCancelled);
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
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(new Route(
            CohesionHttpMethod.Get,
            "/slow",
            new RouterRouteHandler(async context =>
            {
                await Task.Delay(_neverInTestBudget, context.RequestCancelled);
            }),
            new RouterRouteMetadataCollection(new RequestTimeoutMetadata(_globalTimeout))));

        factory.Application.UseRequestTimeouts(_neverInTestBudget);

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
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(new Route(
            CohesionHttpMethod.Get,
            "/steady",
            new RouterRouteHandler(async context =>
            {
                await Task.Delay(_pastGlobalTimeout, context.RequestCancelled);
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("finished"), context.RequestCancelled);
            }),
            new RouterRouteMetadataCollection(new RequestTimeoutMetadata(_neverInTestBudget))));

        factory.Application.UseRequestTimeouts(_globalTimeout);

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
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(new Route(
            CohesionHttpMethod.Get,
            "/unhurried",
            new RouterRouteHandler(async context =>
            {
                await Task.Delay(_pastGlobalTimeout, context.RequestCancelled);
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("unhurried"), context.RequestCancelled);
            }),
            new RouterRouteMetadataCollection(RequestTimeoutMetadata.Disabled)));

        factory.Application.UseRequestTimeouts(_globalTimeout);

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/unhurried", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("unhurried");
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - E2E: Registered before UseRouting, an endpoint timeout should fail the request at dispatch instead of running unbounded")]
    public async Task UseRequestTimeouts_RegisteredBeforeRouting_ShouldFailEndpointWithTimeout()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();

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

        factory.Application.UseRequestTimeouts(_neverInTestBudget);

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(new Route(
            CohesionHttpMethod.Get,
            "/bounded",
            new RouterRouteHandler(context =>
            {
                Interlocked.Increment(ref endpointInvocations);
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                return Task.CompletedTask;
            }),
            new RouterRouteMetadataCollection(new RequestTimeoutMetadata(_globalTimeout))));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/bounded", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        endpointInvocations.ShouldBe(0);
        dispatchFailure.ShouldNotBeNull().Message.ShouldContain("UseRequestTimeouts()", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - E2E: Registered before UseRouting, a disabled endpoint should still run because it requires nothing")]
    public async Task UseRequestTimeouts_RegisteredBeforeRouting_ShouldRunDisabledEndpoint()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();

        factory.Application.UseRequestTimeouts(_neverInTestBudget);

        IRouterBuilder routes = factory.Application.UseRouting();
        routes.Map(new Route(
            CohesionHttpMethod.Get,
            "/stream",
            new RouterRouteHandler(async context =>
            {
                context.Response.StatusCode = CohesionHttpStatusCode.Ok;
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("streamed"), context.RequestCancelled);
            }),
            new RouterRouteMetadataCollection(RequestTimeoutMetadata.Disabled)));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/stream", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("streamed");
    }

    [Fact(DisplayName = "Cohesion Test [Web.RequestTimeouts] - E2E: A client-cancelled request should surface as cancellation, not a timeout response")]
    public async Task UseRequestTimeouts_ClientCancelsRequest_ShouldSurfaceAsCancellation()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        factory.Application.UseRequestTimeouts(TimeSpan.FromSeconds(2));
        factory.Application.Use(async (context, next) =>
        {
            await Task.Delay(_neverInTestBudget, context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();
        using CancellationTokenSource clientCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        clientCancellation.CancelAfter(TimeSpan.FromMilliseconds(150));

        // Act / Assert — the client observes its own cancellation; no 504 arrives.
        await Should.ThrowAsync<TaskCanceledException>(client.GetAsync("/abandoned", clientCancellation.Token));
    }
}
