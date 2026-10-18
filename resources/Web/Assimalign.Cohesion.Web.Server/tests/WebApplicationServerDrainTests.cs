using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web.Internal;
using Assimalign.Cohesion.Web.Server.Tests.TestObjects;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Server.Tests;

/// <summary>
/// The lame-duck drain of <see cref="WebApplicationServer.StopAsync"/> (#146): the stop takes nothing new
/// and lets the exchanges in flight finish within its budget, announcing the close to every peer, and
/// cancels only what outlives the budget. The first two tests pin the server against instrumented
/// doubles; the rest prove it end to end over the in-memory transport.
/// </summary>
public class WebApplicationServerDrainTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Server] - Server/Drain: Connection cancellation lifetimes should be independent and both observe an expired drain budget")]
    public async Task StopAsync_WithTwoConnections_ShouldRelayCancellationToBothLifetimes()
    {
        // Arrange
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int enteredCount = 0;
        CancellationToken[] tokens = new CancellationToken[2];
        FakeHttpContext firstExchange = new();
        FakeHttpContext secondExchange = new();
        FakeHttpConnectionContext firstContext = new(new[] { firstExchange }, parkAfterExchanges: true);
        FakeHttpConnectionContext secondContext = new(new[] { secondExchange }, parkAfterExchanges: true);
        FakeHttpConnection first = new(firstContext);
        FakeHttpConnection second = new(secondContext);
        FakePipeline pipeline = new(async (exchange, token) =>
        {
            tokens[ReferenceEquals(exchange, firstExchange) ? 0 : 1] = token;
            if (Interlocked.Increment(ref enteredCount) == 2) { entered.TrySetResult(); }
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        WebApplicationServer server = CreateServer(pipeline, new FakeHttpConnectionListener(first, second));
        await server.StartAsync();

        try
        {
            await entered.Task.WaitAsync(_timeout);
            tokens[0].ShouldNotBe(tokens[1]);
            using CancellationTokenSource budget = new();
            Task stop = server.StopAsync(budget.Token);
            await Task.WhenAll(firstContext.GracefulCloseRequested, secondContext.GracefulCloseRequested).WaitAsync(_timeout);
            tokens.ShouldAllBe(token => !token.IsCancellationRequested);

            // Act
            budget.Cancel();
            await stop.WaitAsync(_timeout);

            // Assert
            tokens.ShouldAllBe(token => token.IsCancellationRequested);
            firstExchange.CancelCount.ShouldBe(1);
            secondExchange.CancelCount.ShouldBe(1);
            first.DisposeCount.ShouldBe(1);
            second.DisposeCount.ShouldBe(1);
        }
        finally
        {
            await server.StopAsync().WaitAsync(_timeout);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web Server] - Server/Drain: StopAsync begins the graceful close and lets the exchange in flight finish and send its response")]
    public async Task StopAsync_WithExchangeInFlight_ShouldBeginGracefulCloseAndDeliverTheResponse()
    {
        // Arrange — one exchange parked in the pipeline on a gate that ignores cancellation.
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken pipelineToken = default;
        CancellationToken sendToken = default;
        FakeHttpContext exchange = new();
        FakeHttpConnectionContext connectionContext = new(new[] { exchange }, parkAfterExchanges: true)
        {
            SendHandler = (_, cancellationToken) =>
            {
                sendToken = cancellationToken;
                return ValueTask.CompletedTask;
            },
        };
        FakeHttpConnection connection = new(connectionContext);
        FakePipeline pipeline = new(async (_, cancellationToken) =>
        {
            pipelineToken = cancellationToken;
            entered.TrySetResult();
            await release.Task;
        });
        FakeHttpConnectionListener listener = new(connection);
        WebApplicationServer server = CreateServer(pipeline, listener);

        await server.StartAsync();
        await entered.Task.WaitAsync(_timeout);

        // Act
        Task stop = server.StopAsync();
        await connectionContext.GracefulCloseRequested.WaitAsync(_timeout);

        // Assert — the close began, but nothing was cancelled and the stop waits for the exchange.
        pipelineToken.IsCancellationRequested.ShouldBeFalse();
        stop.IsCompleted.ShouldBeFalse();

        release.TrySetResult();
        await stop.WaitAsync(_timeout);

        connectionContext.GracefulCloseCount.ShouldBe(1);
        connectionContext.SendCount.ShouldBe(1);
        sendToken.IsCancellationRequested.ShouldBeFalse();
        exchange.CancelCount.ShouldBe(0);
        connection.AbortCount.ShouldBe(0);
        connection.DisposeCount.ShouldBe(1);
        listener.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web Server] - Server/Drain: When the budget runs out, the exchanges in flight are cancelled and their connections aborted")]
    public async Task StopAsync_WhenBudgetRunsOut_ShouldCancelInFlightExchangesAndAbortTheirConnections()
    {
        // Arrange — one exchange that runs until it is cancelled.
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken pipelineToken = default;
        FakeHttpContext exchange = new();
        FakeHttpConnectionContext connectionContext = new(new[] { exchange }, parkAfterExchanges: true);
        FakeHttpConnection connection = new(connectionContext);
        FakePipeline pipeline = new(async (_, cancellationToken) =>
        {
            pipelineToken = cancellationToken;
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        FakeHttpConnectionListener listener = new(connection);
        WebApplicationServer server = CreateServer(pipeline, listener);

        await server.StartAsync();
        await entered.Task.WaitAsync(_timeout);

        using CancellationTokenSource budget = new();
        Task stop = server.StopAsync(budget.Token);
        await connectionContext.GracefulCloseRequested.WaitAsync(_timeout);
        pipelineToken.IsCancellationRequested.ShouldBeFalse();

        // Act
        budget.Cancel();

        // Assert — the stop completes once the listener is released, the exchange observed the
        // cancellation and was reset, and its connection was aborted.
        await Should.NotThrowAsync(() => stop.WaitAsync(_timeout));
        await connection.Disposed.Task.WaitAsync(_timeout);
        pipelineToken.IsCancellationRequested.ShouldBeTrue();
        exchange.CancelCount.ShouldBe(1);
        connection.AbortCount.ShouldBe(1);
        connection.AbortReason.ShouldBeOfType<ConnectionAbortedException>();
        listener.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Server] - Server/Drain: An HTTP/2 connection gets GOAWAY when the stop begins and its open stream completes")]
    public async Task StopAsync_Http2WithOpenStream_ShouldGoAwayAndCompleteTheStream()
    {
        // Arrange — stream 1 parks in the pipeline; the client reads the server's frames raw.
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using InMemoryConnectionListener transport = new();
        IHttpConnectionListener listener = HttpConnectionListener.Create(options => options.UseHttp2(transport));
        FakePipeline pipeline = new(async (context, _) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);

            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.ASCII.GetBytes("finished"), cancellationToken);
        });
        WebApplicationServer server = CreateServer(pipeline, listener);

        await server.StartAsync(cancellationToken);

        try
        {
            await using Http2RawClient client = await Http2RawClient.ConnectAsync(transport, cancellationToken);
            await client.SendGetAsync(1, "/work", cancellationToken);
            await entered.Task.WaitAsync(cancellationToken);

            // Act
            Task stop = server.StopAsync(CancellationToken.None);

            // Assert — GOAWAY(NO_ERROR) names stream 1 as the last processed while it is still open
            // (RFC 9113 §6.8)...
            Http2RawFrame goAway = await client.ReadUntilAsync(frame => frame.Type == Http2RawFrame.GoAwayType, cancellationToken);
            goAway.GoAwayErrorCode.ShouldBe(0u);
            goAway.GoAwayLastStreamId.ShouldBe(1);
            stop.IsCompleted.ShouldBeFalse();

            // ...and the stream then completes with its full response.
            release.TrySetResult();
            await client.ReadUntilAsync(frame => frame.StreamId == 1 && frame.EndStream, cancellationToken);

            Http2RawFrame[] streamFrames = client.Frames.Where(frame => frame.StreamId == 1).ToArray();
            streamFrames.ShouldContain(frame => frame.Type == Http2RawFrame.HeadersType);
            streamFrames.ShouldNotContain(frame => frame.Type == Http2RawFrame.RstStreamType);
            Encoding.ASCII.GetString(streamFrames
                .Where(frame => frame.Type == Http2RawFrame.DataType)
                .SelectMany(frame => frame.Payload)
                .ToArray()).ShouldBe("finished");

            await stop.WaitAsync(cancellationToken);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }


    private static WebApplicationServer CreateServer(IWebApplicationPipeline pipeline, IHttpConnectionListener listener)
    {
        return new WebApplicationServer(new WebApplicationServerOptions
        {
            Pipeline = pipeline,

        }, listener);
    }
}
