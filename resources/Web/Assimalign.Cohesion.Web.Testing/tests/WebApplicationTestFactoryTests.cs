using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Hosting;

using Shouldly;

using Xunit;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Web.Testing.Tests;

/// <summary>
/// End-to-end coverage for <see cref="WebApplicationTestFactory"/> over HTTP/1.1: requests from
/// the factory's <see cref="HttpClient"/> dial the in-memory listener — no sockets, no ports —
/// and flow the composed application's full pipeline.
/// </summary>
public class WebApplicationTestFactoryTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Testing] - CreateClient: Should flow a request through the full pipeline end to end")]
    public async Task CreateClient_GetRequest_ShouldFlowFullPipelineEndToEnd()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        string? observedPath = null;
        factory.Application.Use(async (context, next) =>
        {
            observedPath = context.Request.Path.ToString();
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;

            byte[] payload = Encoding.UTF8.GetBytes("hello from the pipeline");
            await context.Response.Body.WriteAsync(payload, context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/probe", cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.Version.ShouldBe(NetHttpVersion.Version11);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("hello from the pipeline");
        observedPath.ShouldBe("/probe");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Testing] - CreateClient: Should stream the request body through the pipeline (echo)")]
    public async Task CreateClient_PostRequest_ShouldEchoRequestBody()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        factory.Application.Use(async (context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Request.Body.CopyToAsync(context.Response.Body, context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();
        using StringContent content = new("echo me through the in-memory transport", Encoding.UTF8);

        // Act
        using HttpResponseMessage response = await client.PostAsync("/echo", content, cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("echo me through the in-memory transport");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Testing] - CreateClient: Sequential requests should reuse one keep-alive connection")]
    public async Task CreateClient_SequentialRequests_ShouldReuseKeepAliveConnection()
    {
        // Arrange — the in-memory driver mints a distinct ephemeral client endpoint per dialed
        // connection, so the remote endpoint the server observes identifies the connection.
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        ConcurrentQueue<string?> observedConnections = new();
        factory.Application.Use((context, next) =>
        {
            observedConnections.Enqueue(context.ConnectionInfo.RemoteEndPoint?.ToString());
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();

        // Act — sequential requests on one client ride the pooled keep-alive connection.
        (await client.GetAsync("/first", cancellationToken)).Dispose();
        (await client.GetAsync("/second", cancellationToken)).Dispose();

        // Assert
        observedConnections.Count.ShouldBe(2);
        observedConnections.Distinct().Count().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Testing] - CreateClient: Should start the factory when it has not been started yet")]
    public async Task CreateClient_BeforeStart_ShouldStartTheFactory()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });

        factory.IsStarted.ShouldBeFalse();

        // Act
        using HttpClient client = factory.CreateClient();

        // Assert
        factory.IsStarted.ShouldBeTrue();
        using HttpResponseMessage response = await client.GetAsync("/", cancellationToken);
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Testing] - StartAsync: Repeated starts should be idempotent")]
    public async Task StartAsync_CalledTwice_ShouldBeIdempotent()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();
        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });

        // Act
        await factory.StartAsync(cancellationToken);
        await factory.StartAsync(cancellationToken);

        // Assert — the factory still serves normally.
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/", cancellationToken);
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Testing] - DisposeAsync: Should stop serving and refuse new dials")]
    public async Task DisposeAsync_AfterServing_ShouldRefuseNewDials()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        WebApplicationTestFactory factory = new();
        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();
        (await client.GetAsync("/before", cancellationToken)).Dispose();

        // Act
        await factory.DisposeAsync();
        await factory.DisposeAsync(); // double dispose is safe

        // Assert — the listener is torn down, so a new request cannot connect.
        await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync("/after", cancellationToken));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Testing] - DisposeAsync: Should drain promptly with an idle keep-alive connection parked")]
    public async Task DisposeAsync_WithIdleKeepAliveConnection_ShouldCompletePromptly()
    {
        // Arrange — after a completed request the pooled connection parks idle in the server's
        // receive loop; disposal must unblock it rather than hang the drain on it.
        using CancellationTokenSource cancellation = new(TestTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        WebApplicationTestFactory factory = new();
        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();
        (await client.GetAsync("/", cancellationToken)).Dispose();

        // Act & Assert — bounded by the test timeout; a hung drain fails the test.
        await factory.DisposeAsync().AsTask().WaitAsync(cancellationToken);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Testing] - Options: An out-of-range protocol should be rejected at construction")]
    public void Constructor_UndefinedProtocol_ShouldThrowArgumentException()
    {
        // Arrange
        WebApplicationTestFactoryOptions options = new()
        {
            Protocol = (WebApplicationTestProtocol)42,
        };

        // Act & Assert
        Should.Throw<ArgumentException>(() => new WebApplicationTestFactory(options));
    }
}
