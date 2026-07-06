using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public class WebApplicationServerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: An idle keep-alive connection does not starve other connections")]
    public async Task StartAsync_WithIdleKeepAliveConnection_ServesOtherConnectionsConcurrently()
    {
        // Arrange — connection A serves one request then parks forever (idle keep-alive); connection
        // B serves one request and completes. Serial dispatch would leave B stuck behind A's park.
        FakeHttpContext exchangeA = new();
        FakeHttpContext exchangeB = new();

        FakeHttpConnection connectionA = new(new FakeHttpConnectionContext(new[] { exchangeA }, parkAfterExchanges: true));
        FakeHttpConnection connectionB = new(new FakeHttpConnectionContext(new[] { exchangeB }));

        TaskCompletionSource exchangeBProcessed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakePipeline pipeline = new((context, _) =>
        {
            if (ReferenceEquals(context, exchangeB))
            {
                exchangeBProcessed.TrySetResult();
            }

            return Task.CompletedTask;
        });

        FakeHttpConnectionListener listener = new(connectionA, connectionB);
        WebApplicationServer server = CreateServer(pipeline, listener);

        // Act
        await server.StartAsync();

        // Assert — B is served within the bound even though A is still parked.
        await Should.NotThrowAsync(() => exchangeBProcessed.Task.WaitAsync(Timeout));
        connectionB.Context.SendCount.ShouldBe(1);

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: A pipeline exception tears down only that connection and the loop keeps serving")]
    public async Task StartAsync_WhenPipelineThrows_TearsDownOnlyThatConnectionAndKeepsServing()
    {
        // Arrange — A's exchange throws from the pipeline; B's is served normally.
        FakeHttpContext exchangeA = new();
        FakeHttpContext exchangeB = new();

        FakeHttpConnection connectionA = new(new FakeHttpConnectionContext(new[] { exchangeA }));
        FakeHttpConnection connectionB = new(new FakeHttpConnectionContext(new[] { exchangeB }));

        InvalidOperationException fault = new("pipeline boom");
        TaskCompletionSource exchangeBProcessed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakePipeline pipeline = new((context, _) =>
        {
            if (ReferenceEquals(context, exchangeA))
            {
                throw fault;
            }

            if (ReferenceEquals(context, exchangeB))
            {
                exchangeBProcessed.TrySetResult();
            }

            return Task.CompletedTask;
        });

        FakeHttpConnectionListener listener = new(connectionA, connectionB);
        WebApplicationServer server = CreateServer(pipeline, listener);

        // Act
        await server.StartAsync();

        // Assert — the faulted connection is aborted and disposed; the survivor is served.
        await Should.NotThrowAsync(() => connectionA.Disposed.Task.WaitAsync(Timeout));
        connectionA.AbortCount.ShouldBe(1);
        connectionA.AbortReason.ShouldBeSameAs(fault);
        connectionA.Context.DisposeCount.ShouldBe(1);
        exchangeA.DisposeCount.ShouldBe(1);

        await Should.NotThrowAsync(() => exchangeBProcessed.Task.WaitAsync(Timeout));
        await Should.NotThrowAsync(() => connectionB.Disposed.Task.WaitAsync(Timeout));
        connectionB.AbortCount.ShouldBe(0);
        connectionB.Context.SendCount.ShouldBe(1);

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: A connection and its context are disposed when the loop ends normally")]
    public async Task ServeConnection_OnNormalCompletion_DisposesConnectionAndContext()
    {
        // Arrange
        FakeHttpContext exchange = new();
        FakeHttpConnection connection = new(new FakeHttpConnectionContext(new[] { exchange }));
        FakePipeline pipeline = new();
        FakeHttpConnectionListener listener = new(connection);
        WebApplicationServer server = CreateServer(pipeline, listener);

        // Act
        await server.StartAsync();
        await Should.NotThrowAsync(() => connection.Disposed.Task.WaitAsync(Timeout));

        // Assert
        connection.DisposeCount.ShouldBe(1);
        connection.Context.DisposeCount.ShouldBe(1);
        connection.AbortCount.ShouldBe(0);
        exchange.DisposeCount.ShouldBe(1);
        connection.Context.SendCount.ShouldBe(1);

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: A connection that opens and closes without a request is still disposed")]
    public async Task ServeConnection_OnClientDisconnectBeforeRequest_DisposesConnectionAndContext()
    {
        // Arrange — the peer opens then closes without sending; the receive sequence completes empty.
        FakeHttpConnection connection = new(new FakeHttpConnectionContext());
        FakePipeline pipeline = new();
        FakeHttpConnectionListener listener = new(connection);
        WebApplicationServer server = CreateServer(pipeline, listener);

        // Act
        await server.StartAsync();
        await Should.NotThrowAsync(() => connection.Disposed.Task.WaitAsync(Timeout));

        // Assert
        connection.DisposeCount.ShouldBe(1);
        connection.Context.DisposeCount.ShouldBe(1);
        connection.Context.SendCount.ShouldBe(0);
        connection.AbortCount.ShouldBe(0);

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: StopAsync drains an in-flight connection and disposes the listener")]
    public async Task StopAsync_WithInFlightConnection_DrainsAndDisposesListenerWithoutEscapedException()
    {
        // Arrange — one connection parked as an idle keep-alive when the stop is requested.
        FakeHttpContext exchange = new();
        FakeHttpConnection connection = new(new FakeHttpConnectionContext(new[] { exchange }, parkAfterExchanges: true));
        TaskCompletionSource exchangeProcessed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakePipeline pipeline = new((_, _) =>
        {
            exchangeProcessed.TrySetResult();
            return Task.CompletedTask;
        });
        FakeHttpConnectionListener listener = new(connection);
        WebApplicationServer server = CreateServer(pipeline, listener);

        await server.StartAsync();
        await Should.NotThrowAsync(() => exchangeProcessed.Task.WaitAsync(Timeout));

        // Act — the parked connection must be drained by the stop, not hang it.
        await Should.NotThrowAsync(() => server.StopAsync().WaitAsync(Timeout));

        // Assert — graceful drain: connection disposed, listener disposed, no escaped exception.
        connection.DisposeCount.ShouldBe(1);
        connection.Context.DisposeCount.ShouldBe(1);
        listener.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: StopAsync before StartAsync is a no-op")]
    public async Task StopAsync_WithoutStart_DoesNotThrow()
    {
        WebApplicationServer server = CreateServer(new FakePipeline(), new FakeHttpConnectionListener());

        await Should.NotThrowAsync(() => server.StopAsync().WaitAsync(Timeout));
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: MaxConcurrentConnections holds back connections beyond the cap until a slot frees")]
    public async Task StartAsync_WithMaxConcurrentConnections_DoesNotOpenBeyondTheCapUntilASlotFrees()
    {
        // Arrange — cap of 1. Connection A holds its slot until the test releases it; connection B
        // must not be opened while A is active, then must be opened once A completes.
        TaskCompletionSource releaseA = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHttpConnection connectionA = new(new FakeHttpConnectionContext(holdUntil: releaseA.Task));

        FakeHttpContext exchangeB = new();
        FakeHttpConnection connectionB = new(new FakeHttpConnectionContext(new[] { exchangeB }));

        TaskCompletionSource exchangeBProcessed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakePipeline pipeline = new((context, _) =>
        {
            if (ReferenceEquals(context, exchangeB))
            {
                exchangeBProcessed.TrySetResult();
            }

            return Task.CompletedTask;
        });

        FakeHttpConnectionListener listener = new(connectionA, connectionB);
        WebApplicationServer server = CreateServer(pipeline, listener, maxConcurrentConnections: 1);

        // Act — start and let A take the only slot.
        await server.StartAsync();
        await Should.NotThrowAsync(() => WaitForAsync(() => connectionA.OpenCount == 1, Timeout));

        // Assert — B stays in the backlog: not opened while the slot is held.
        await Task.Delay(250);
        connectionB.OpenCount.ShouldBe(0);
        exchangeBProcessed.Task.IsCompleted.ShouldBeFalse();

        // Act — free the slot; B is now accepted, opened, and served.
        releaseA.TrySetResult();

        // Assert
        await Should.NotThrowAsync(() => exchangeBProcessed.Task.WaitAsync(Timeout));
        connectionB.OpenCount.ShouldBe(1);

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: The constructor rejects a missing pipeline")]
    public void Constructor_WithoutPipeline_Throws()
    {
        WebApplicationServerOptions options = new() { Listener = new FakeHttpConnectionListener() };

        Should.Throw<ArgumentException>(() => new WebApplicationServer(options));
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: The constructor rejects a missing listener")]
    public void Constructor_WithoutListener_Throws()
    {
        WebApplicationServerOptions options = new() { Pipeline = new FakePipeline() };

        Should.Throw<ArgumentException>(() => new WebApplicationServer(options));
    }

    [Theory(DisplayName = "Cohesion Test [Web Hosting] - Server: The constructor rejects a non-positive concurrency cap")]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithNonPositiveMaxConcurrentConnections_Throws(int maxConcurrentConnections)
    {
        WebApplicationServerOptions options = new()
        {
            Pipeline = new FakePipeline(),
            Listener = new FakeHttpConnectionListener(),
            MaxConcurrentConnections = maxConcurrentConnections
        };

        Should.Throw<ArgumentOutOfRangeException>(() => new WebApplicationServer(options));
    }

    private static WebApplicationServer CreateServer(
        IWebApplicationPipeline pipeline,
        FakeHttpConnectionListener listener,
        int? maxConcurrentConnections = null)
    {
        return new WebApplicationServer(new WebApplicationServerOptions
        {
            Pipeline = pipeline,
            Listener = listener,
            MaxConcurrentConnections = maxConcurrentConnections
        });
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        using CancellationTokenSource cancellation = new(timeout);

        while (!condition())
        {
            cancellation.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellation.Token).ConfigureAwait(false);
        }
    }
}
