using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// Covers the pipeline terminal that replaced the silent <c>Task.CompletedTask</c> in
/// <c>WebApplication.Build</c> (issue #881): an unhandled request now yields a bodyless
/// <c>404 Not Found</c> instead of an empty <c>200</c>, while a response a middleware already shaped
/// (status, body, redirect) is left untouched. The 404 is deliberately payload-free — the
/// hosting-isolation rule keeps <c>Web.ProblemDetails</c> out of this runtime module, so the opt-in
/// status-code-pages middleware (in <c>Web.ErrorHandling</c>) is what upgrades it to problem+json.
/// </summary>
public class WebApplicationTerminalFallbackTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Terminal: An unhandled request should yield a bodyless 404")]
    public async Task Terminal_WhenNoMiddlewareHandles_ShouldReturnBodyless404()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/missing", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Terminal: A status a middleware already chose should not be overwritten")]
    public async Task Terminal_WhenMiddlewareChoseStatus_ShouldNotOverwrite()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.NoContent;
            return next.Invoke(context);
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/chosen", cancellationToken);

        // Assert — the terminal leaves the middleware's 204 in place rather than forcing a 404.
        response.StatusCode.ShouldBe(NetHttpStatusCode.NoContent);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Terminal: A body a middleware already wrote should not be overwritten")]
    public async Task Terminal_WhenMiddlewareWroteBody_ShouldNotOverwrite()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Application.Use(async (context, next) =>
        {
            context.Response.Headers[Assimalign.Cohesion.Http.HttpHeaderKey.ContentType] = "text/plain; charset=utf-8";
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("hello"), context.RequestCancelled);
            await next.Invoke(context);
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/written", cancellationToken);

        // Assert — a 200 body survives; the terminal does not turn it into a 404.
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("hello");
    }
}
