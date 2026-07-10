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
/// Full-pipeline integration coverage for the <c>WebApplicationServer</c> dispatch and stop
/// semantics (issue #762), driven end to end over the in-memory transport through
/// <see cref="WebApplicationTestFactory"/>. The unit suite pins the same properties against
/// instrumented doubles; this suite proves them with a real client, real HTTP/1.1 exchanges,
/// and the real accept loop.
/// </summary>
public class WebApplicationServerIntegrationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server: A parked connection should not starve other connections (per-connection dispatch)")]
    public async Task Server_ParkedConnection_ShouldNotStarveOtherConnections()
    {
        // Arrange — request /slow parks in the pipeline on a test-owned gate; /fast must be
        // served from a second connection while the first is parked. Pre-#762 this deadlocked:
        // the accept loop served connections inline, so the parked connection blocked all
        // others.
        using CancellationTokenSource cancellation = new(TestTimeout);
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

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server: StopAsync should wait for an in-flight request to unwind (graceful drain)")]
    public async Task StopAsync_WithInFlightRequest_ShouldWaitForItToUnwind()
    {
        // Arrange — an exchange parked in the pipeline on a test-owned gate that deliberately
        // ignores cancellation, so the only way StopAsync can complete is by draining it.
        using CancellationTokenSource cancellation = new(TestTimeout);
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
        using CancellationTokenSource cancellation = new(TestTimeout);
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
        using CancellationTokenSource cancellation = new(TestTimeout);
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
}
