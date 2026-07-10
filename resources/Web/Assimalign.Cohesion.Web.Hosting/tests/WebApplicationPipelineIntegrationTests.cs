using System;
using System.Collections.Concurrent;
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
/// Full-pipeline integration coverage for middleware composition, driven end to end over the
/// in-memory transport through <see cref="WebApplicationTestFactory"/> (no sockets, no
/// ports): real client, real HTTP/1.1 wire exchange, real server dispatch.
/// </summary>
public class WebApplicationPipelineIntegrationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Pipeline: Middleware should run in registration (onion) order end to end")]
    public async Task Pipeline_MultipleMiddleware_ShouldRunInRegistrationOnionOrder()
    {
        // Arrange — two wrapping middleware around a terminal handler; each records entry and
        // exit so both the inbound order and the unwind order are observable.
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        ConcurrentQueue<string> order = new();
        WebApplication app = factory.Application;

        app.Use(async (context, next) =>
        {
            order.Enqueue("outer:in");
            await next.Invoke(context);
            order.Enqueue("outer:out");
        });
        app.Use(async (context, next) =>
        {
            order.Enqueue("inner:in");
            await next.Invoke(context);
            order.Enqueue("inner:out");
        });
        app.Use(async (context, next) =>
        {
            order.Enqueue("terminal");
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;

            byte[] payload = Encoding.UTF8.GetBytes("onion");
            await context.Response.Body.WriteAsync(payload, context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/order", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("onion");
        order.ShouldBe(new[] { "outer:in", "inner:in", "terminal", "inner:out", "outer:out" });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Pipeline: A short-circuiting middleware should skip everything downstream")]
    public async Task Pipeline_ShortCircuitingMiddleware_ShouldSkipDownstreamMiddleware()
    {
        // Arrange — the first middleware answers 403 without calling next; the downstream
        // middleware records whether it ever ran.
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        bool downstreamRan = false;
        WebApplication app = factory.Application;

        app.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Forbidden;
            return Task.CompletedTask;
        });
        app.Use((context, next) =>
        {
            downstreamRan = true;
            return next.Invoke(context);
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/guarded", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
        downstreamRan.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Pipeline: A middleware fault should cost only its own connection, not the server")]
    public async Task Pipeline_MiddlewareThrows_ShouldIsolateFaultToItsConnection()
    {
        // Arrange — the application-exception isolation boundary (#762): a throwing exchange
        // tears down its own connection while the accept loop keeps serving new ones.
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        factory.Application.Use((context, next) =>
        {
            if (context.Request.Path.ToString() == "/faulty")
            {
                throw new InvalidOperationException("Deliberate application fault.");
            }

            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });

        using HttpClient faultyClient = factory.CreateClient();
        using HttpClient healthyClient = factory.CreateClient();

        // Act & Assert — the faulting exchange surfaces as a transport-level failure on its
        // own connection...
        await Should.ThrowAsync<HttpRequestException>(() => faultyClient.GetAsync("/faulty", cancellationToken));

        // ...and the server keeps serving fresh connections afterwards.
        using HttpResponseMessage healthy = await healthyClient.GetAsync("/healthy", cancellationToken);
        healthy.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }
}
