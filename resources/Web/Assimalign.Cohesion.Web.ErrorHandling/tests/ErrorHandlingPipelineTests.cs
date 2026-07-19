using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.ErrorHandling.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.ErrorHandling.Tests;

/// <summary>
/// Full-pipeline coverage over the in-memory test factory: the hook is composed at builder time,
/// seeded onto each exchange by the runtime, and invoked by a boundary middleware the way the
/// exception-boundary feature (#881) will consume it.
/// </summary>
public class ErrorHandlingPipelineTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static void UseErrorBoundary(WebApplicationTestFactory factory)
    {
        factory.Application.Use(async (context, next) =>
        {
            try
            {
                await next.Invoke(context);
            }
            catch (Exception exception)
            {
                IErrorHandlingFeature hook = context.Features.Get<IErrorHandlingFeature>()!;
                await hook.HandleAsync(context, exception, context.RequestCancelled);
            }
        });
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Pipeline: Should render an escaped fault as problem+json through the default")]
    public async Task Pipeline_UnhandledFault_ShouldRenderProblemJsonDefault()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddErrorHandling();

        UseErrorBoundary(factory);
        factory.Application.Use((context, next) => throw new InvalidOperationException("the key ring is unavailable"));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/faulting", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType.ShouldNotBeNull();
        response.Content.Headers.ContentType.ToString().ShouldBe("application/problem+json");

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        using JsonDocument document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("type").GetString().ShouldBe("about:blank");
        document.RootElement.GetProperty("title").GetString().ShouldBe("Internal Server Error");
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(500);
        body.ShouldNotContain("key ring");
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - Pipeline: Should let a registered OnError handler own the fault response")]
    public async Task Pipeline_RegisteredHandler_ShouldOwnFaultResponse()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddErrorHandling().OnError(async (context, exception, token) =>
        {
            if (exception is not TimeoutException)
            {
                return false;
            }

            context.Response.StatusCode = 503;
            context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain; charset=utf-8";
            await context.Response.Body.WriteAsync("upstream timed out"u8.ToArray(), token);
            return true;
        });

        UseErrorBoundary(factory);
        factory.Application.Use((context, next) => throw new TimeoutException("upstream"));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/faulting", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("upstream timed out");
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - UseErrorHandling: Should catch a downstream fault and render problem+json end to end")]
    public async Task UseErrorHandling_UnhandledFault_ShouldRenderProblemJson500()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddErrorHandling();
        factory.Application.UseErrorHandling();
        factory.Application.Use((context, next) => throw new InvalidOperationException("the key ring is unavailable"));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/faulting", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.ToString().ShouldBe("application/problem+json");

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        using JsonDocument document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(500);
        body.ShouldNotContain("key ring", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - UseErrorHandling: A registered OnError handler should own the response through the boundary verb")]
    public async Task UseErrorHandling_RegisteredHandler_ShouldOwnResponse()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Builder.AddErrorHandling().OnError(async (context, exception, token) =>
        {
            if (exception is not TimeoutException)
            {
                return false;
            }

            context.Response.StatusCode = 503;
            context.Response.Headers[HttpHeaderKey.ContentType] = "text/plain; charset=utf-8";
            await context.Response.Body.WriteAsync("upstream timed out"u8.ToArray(), token);
            return true;
        });
        factory.Application.UseErrorHandling();
        factory.Application.Use((context, next) => throw new TimeoutException("upstream"));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/faulting", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("upstream timed out");
    }

    [Fact(DisplayName = "Cohesion Test [Web.ErrorHandling] - UseStatusCodePages: Should upgrade the pipeline's bodyless 404 terminal to problem+json")]
    public async Task UseStatusCodePages_UnmatchedRequest_ShouldUpgradeTerminal404ToProblemJson()
    {
        // Arrange — no routing/handler middleware, so the request reaches the Web.Hosting terminal,
        // which sets a bodyless 404; the status-code-pages verb upgrades it to problem+json. This is
        // the cross-package layering the hosting-isolation rule mandates.
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Application.UseStatusCodePages();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/missing", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.ToString().ShouldBe("application/problem+json");

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(404);
        document.RootElement.GetProperty("title").GetString().ShouldBe("Not Found");
    }
}
