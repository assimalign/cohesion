using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// Full-pipeline integration coverage for the <c>WebApplicationServer</c> dispatch and stop
/// semantics (issues #762 and #1049), driven end to end over the in-memory transport through
/// <see cref="WebApplicationTestFactory"/>. The unit suite pins the same properties against
/// instrumented doubles; this suite proves them with a real client, real HTTP/1.1 exchanges,
/// and the real accept loop. The pipelining test writes raw bytes instead, because a real client
/// never pipelines — and pipelining is what shows the server asks for the next request only after
/// the previous response.
/// </summary>
public class WebApplicationServerIntegrationTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server: A parked connection should not starve other connections (per-connection dispatch)")]
    public async Task Server_ParkedConnection_ShouldNotStarveOtherConnections()
    {
        // Arrange — request /slow parks in the pipeline on a test-owned gate; /fast must be
        // served from a second connection while the first is parked. Pre-#762 this deadlocked:
        // the accept loop served connections inline, so the parked connection blocked all
        // others.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        TaskCompletionSource slowEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using WebApplicationTestFactory factory = new();

        factory.Application.Use(async (context, next) =>
        {
            if (context.Request.Path.ToString() == "/slow")
            {
                slowEntered.TrySetResult();
                await release.Task.WaitAsync(context.RequestCancelled);
            }

            context.Response.StatusCode = CohesionHttpStatusCode.Ok;

            byte[] payload = Encoding.UTF8.GetBytes(context.Request.Path.ToString());
            await context.Response.Body.WriteAsync(payload, context.RequestCancelled);
        });

        // Distinct clients own distinct connection pools, so the two requests are guaranteed
        // to ride two distinct in-memory connections.
        using HttpClient slowClient = factory.CreateClient();
        using HttpClient fastClient = factory.CreateClient();

        // Act — park /slow in the pipeline, then serve /fast on another connection.
        Task<HttpResponseMessage> slowResponse = slowClient.GetAsync("/slow", cancellationToken);
        await slowEntered.Task.WaitAsync(cancellationToken);

        using HttpResponseMessage fastResponse = await fastClient.GetAsync("/fast", cancellationToken);

        // Assert — the fast connection completed while the slow one was still parked.
        fastResponse.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        slowResponse.IsCompleted.ShouldBeFalse();

        // Release the parked request; its connection finishes normally.
        release.TrySetResult();
        using HttpResponseMessage completedSlow = await slowResponse.WaitAsync(cancellationToken);
        completedSlow.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await completedSlow.Content.ReadAsStringAsync(cancellationToken)).ShouldBe("/slow");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server: Pipelined HTTP/1.1 requests should be served in order, the next only after the previous response")]
    public async Task Server_PipelinedHttp1Requests_ShouldBeServedInOrderOneAtATime()
    {
        // Arrange — two requests pipelined in one write on one raw in-memory connection. /first parks
        // in the pipeline; an HTTP/1.1 connection carries one exchange at a time, so /second must not
        // reach the pipeline until /first's response has been written.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using InMemoryConnectionListener transport = new();
        IHttpConnectionListener listener = HttpConnectionListener.Create(options => options.UseHttp1(transport));
        FakePipeline pipeline = new(async (context, _) =>
        {
            string path = context.Request.Path.ToString();

            if (path == "/first")
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }
            else
            {
                secondEntered.TrySetResult();
            }

            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.ASCII.GetBytes(path), cancellationToken);
        });

        WebApplicationServer server = new(new WebApplicationServerOptions
        {
            Pipeline = pipeline,
            Listener = listener,
        });

        await server.StartAsync(cancellationToken);

        try
        {
            await using Connection client = await transport.CreateFactory().ConnectAsync(transport.EndPoint, cancellationToken);
            Stream stream = client.AsStream();

            // Act
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes(
                    "GET /first HTTP/1.1\r\nHost: localhost\r\n\r\n" +
                    "GET /second HTTP/1.1\r\nHost: localhost\r\n\r\n"),
                cancellationToken);
            await stream.FlushAsync(cancellationToken);

            await firstEntered.Task.WaitAsync(cancellationToken);
            await Task.Delay(200, cancellationToken);

            // Assert — /second is still waiting behind /first...
            secondEntered.Task.IsCompleted.ShouldBeFalse();

            releaseFirst.TrySetResult();

            // ...and the responses arrive in request order once /first is released.
            string responses = await ReadUntilAsync(stream, text => text.EndsWith("/second", StringComparison.Ordinal), cancellationToken);

            responses.Split("HTTP/1.1 200").Length.ShouldBe(3);
            responses.IndexOf("/first", StringComparison.Ordinal).ShouldBeLessThan(responses.IndexOf("/second", StringComparison.Ordinal));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server: StopAsync should wait for an in-flight request to unwind (graceful drain)")]
    public async Task StopAsync_WithInFlightRequest_ShouldWaitForItToUnwind()
    {
        // Arrange — an exchange parked in the pipeline on a test-owned gate that deliberately
        // ignores cancellation, so the only way StopAsync can complete is by draining it.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using WebApplicationTestFactory factory = new();

        factory.Application.Use(async (context, next) =>
        {
            entered.TrySetResult();
            await release.Task;

            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
        });

        using HttpClient client = factory.CreateClient();

        Task<HttpResponseMessage> requestTask = client.GetAsync("/parked", cancellationToken);
        await entered.Task.WaitAsync(cancellationToken);

        // Act — begin the stop while the exchange is parked. The drain must not complete
        // until the in-flight pipeline invocation returns.
        Task stopTask = factory.StopAsync(CancellationToken.None);

        await Task.Delay(100, cancellationToken);
        stopTask.IsCompleted.ShouldBeFalse();

        release.TrySetResult();
        await stopTask.WaitAsync(cancellationToken);

        // Assert — the client's request completes rather than hanging. Whether it observes a
        // response or a torn-down connection is transport detail: the shutdown token governs
        // the post-pipeline send, so the connection may close before the response head is
        // written (cancellation-as-drain — see Web.Hosting docs/DESIGN.md "Stop semantics").
        try
        {
            (await requestTask.WaitAsync(cancellationToken)).Dispose();
        }
        catch (HttpRequestException)
        {
            // The drained connection closed before a response was delivered — acceptable.
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server: StopAsync should complete promptly with an idle keep-alive connection parked")]
    public async Task StopAsync_WithIdleKeepAliveConnection_ShouldCompletePromptly()
    {
        // Arrange — a completed request leaves its pooled connection parked in the server's
        // receive loop; the drain must unblock it rather than hang on the idle client.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();
        (await client.GetAsync("/warmup", cancellationToken)).Dispose();

        // Act & Assert — bounded by the test timeout; a drain hung on the idle keep-alive
        // connection fails the test.
        await factory.StopAsync(CancellationToken.None).WaitAsync(cancellationToken);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server: After StopAsync new connections should be refused")]
    public async Task StopAsync_AfterStop_ShouldRefuseNewConnections()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using WebApplicationTestFactory factory = new();

        factory.Application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });

        using HttpClient client = factory.CreateClient();
        (await client.GetAsync("/before", cancellationToken)).Dispose();

        // Act — stopping the server disposes the listener chain down to the in-memory
        // transport listener.
        await factory.StopAsync(CancellationToken.None).WaitAsync(cancellationToken);

        // Assert — a new request cannot dial the disposed listener.
        await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync("/after", cancellationToken));
    }

    private static async Task<string> ReadUntilAsync(Stream stream, Func<string, bool> isComplete, CancellationToken cancellationToken)
    {
        StringBuilder received = new();
        byte[] buffer = new byte[1024];

        while (!isComplete(received.ToString()))
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);

            if (read == 0)
            {
                break;
            }

            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return received.ToString();
    }
}
