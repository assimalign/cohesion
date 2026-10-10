using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public class WebApplicationServerTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: StartAsync should await listener binding before accepting")]
    public async Task StartAsync_WithPendingBind_ShouldWaitBeforeAccepting()
    {
        // Arrange
        TaskCompletionSource bindEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseBind = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHttpConnectionListener listener = new()
        {
            BindHandler = async cancellationToken =>
            {
                bindEntered.TrySetResult();
                await releaseBind.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        };
        WebApplicationServer server = CreateServer(new FakePipeline(), listener);

        // Act
        Task startTask = server.StartAsync();
        await bindEntered.Task.WaitAsync(_timeout);

        // Assert
        startTask.IsCompleted.ShouldBeFalse();
        listener.AcceptCount.ShouldBe(0);

        releaseBind.TrySetResult();
        await startTask.WaitAsync(_timeout);
        await WaitForAsync(() => listener.AcceptCount > 0, _timeout);
        listener.BindCount.ShouldBe(1);

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: StartAsync should surface a typed listener bind failure")]
    public async Task StartAsync_WhenBindFails_ShouldThrowHostStartupExceptionWithoutAccepting()
    {
        // Arrange
        InvalidOperationException failure = new("endpoint unavailable");
        FakeHttpConnectionListener listener = new()
        {
            BindHandler = _ => ValueTask.FromException(failure)
        };
        WebApplicationServer server = CreateServer(new FakePipeline(), listener);

        // Act
        HostStartupException observed = await Should.ThrowAsync<HostStartupException>(
            () => server.StartAsync());

        // Assert
        observed.InnerException.ShouldBeSameAs(failure);
        listener.BindCount.ShouldBe(1);
        listener.AcceptCount.ShouldBe(0);

        await server.StopAsync();
        listener.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: StopAsync during binding should cancel startup and release without accepting")]
    public async Task StopAsync_WhileBindIsPending_ShouldCancelBindingAndReleaseWithoutAccepting()
    {
        // Arrange
        TaskCompletionSource bindEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource bindCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHttpConnectionListener listener = new()
        {
            BindHandler = async cancellationToken =>
            {
                bindEntered.TrySetResult();

                try
                {
                    await Task.Delay(global::System.Threading.Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    bindCancelled.TrySetResult();
                    throw;
                }
            }
        };
        WebApplicationServer server = CreateServer(new FakePipeline(), listener);

        Task startTask = server.StartAsync();
        await bindEntered.Task.WaitAsync(_timeout);

        // Act
        Task stopTask = server.StopAsync();

        // Assert
        await Task.WhenAll(startTask, stopTask).WaitAsync(_timeout);
        await bindCancelled.Task.WaitAsync(_timeout);
        listener.AcceptCount.ShouldBe(0);
        listener.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: Concurrent StopAsync callers should await the same listener release")]
    public async Task StopAsync_WhenCalledConcurrently_ShouldShareCompletionThroughListenerRelease()
    {
        // Arrange
        TaskCompletionSource disposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseDispose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHttpConnectionListener listener = new()
        {
            DisposeHandler = async () =>
            {
                disposeEntered.TrySetResult();
                await releaseDispose.Task.ConfigureAwait(false);
            }
        };
        WebApplicationServer server = CreateServer(new FakePipeline(), listener);
        await server.StartAsync();
        await WaitForAsync(() => listener.AcceptCount > 0, _timeout);

        // Act
        Task firstStop = server.StopAsync();
        await disposeEntered.Task.WaitAsync(_timeout);
        Task secondStop = server.StopAsync();

        // Assert
        secondStop.ShouldBeSameAs(firstStop);
        secondStop.IsCompleted.ShouldBeFalse();

        releaseDispose.TrySetResult();
        await Task.WhenAll(firstStop, secondStop).WaitAsync(_timeout);
        listener.DisposeCount.ShouldBe(1);
    }

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
        await Should.NotThrowAsync(() => exchangeBProcessed.Task.WaitAsync(_timeout));
        connectionB.Context.SendCount.ShouldBe(1);

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: A pipeline exception fails only its exchange with a 500 and the connection keeps serving")]
    public async Task StartAsync_WhenPipelineThrows_AnswersThatExchangeWith500AndKeepsServing()
    {
        // Arrange — on keep-alive connection A the first exchange throws after staging a partial
        // response, and the second is served normally; connection B is untouched.
        FakeHttpResponse faultedResponse = new();
        faultedResponse.Headers[HttpHeaderKey.ContentType] = "application/json";
        await faultedResponse.Body.WriteAsync(new byte[] { 1, 2, 3 });

        FakeHttpContext faulted = new(response: faultedResponse);
        FakeHttpContext keepAlive = new();
        FakeHttpContext exchangeB = new();

        FakeHttpConnection connectionA = new(new FakeHttpConnectionContext(new[] { faulted, keepAlive }));
        FakeHttpConnection connectionB = new(new FakeHttpConnectionContext(new[] { exchangeB }));

        FakePipeline pipeline = new((context, _) =>
        {
            if (ReferenceEquals(context, faulted))
            {
                throw new InvalidOperationException("pipeline boom");
            }

            return Task.CompletedTask;
        });

        FakeHttpConnectionListener listener = new(connectionA, connectionB);
        WebApplicationServer server = CreateServer(pipeline, listener);

        // Act
        await server.StartAsync();

        // Assert — the faulted exchange is answered with a bare 500 in place of what it staged, and
        // the same connection goes on to serve the next request; nothing is aborted.
        await Should.NotThrowAsync(() => connectionA.Disposed.Task.WaitAsync(_timeout));
        faultedResponse.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        faultedResponse.Headers.Count.ShouldBe(0);
        faultedResponse.Body.Length.ShouldBe(0);
        faulted.CancelCount.ShouldBe(0);
        faulted.DisposeCount.ShouldBe(1);
        pipeline.Executed.ShouldContain(keepAlive);
        keepAlive.DisposeCount.ShouldBe(1);
        connectionA.Context.SendCount.ShouldBe(2);
        connectionA.AbortCount.ShouldBe(0);
        connectionA.Context.DisposeCount.ShouldBe(1);

        await Should.NotThrowAsync(() => connectionB.Disposed.Task.WaitAsync(_timeout));
        connectionB.AbortCount.ShouldBe(0);
        connectionB.Context.SendCount.ShouldBe(1);

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: A faulted HTTP/1.1 exchange whose response cannot be replaced is reset and ends its connection")]
    public async Task StartAsync_WhenSequentialFaultCannotBeAnswered_ResetsTheExchangeAndEndsTheConnection()
    {
        // Arrange — the faulted exchange exposes no usable response, so no 500 can replace it: the
        // server resets it, and a sequential connection ends with a reset exchange.
        FakeHttpContext faulted = new();
        FakeHttpContext next = new();
        FakeHttpConnection connection = new(new FakeHttpConnectionContext(new[] { faulted, next }));
        FakePipeline pipeline = new((context, _) => ReferenceEquals(context, faulted)
            ? throw new InvalidOperationException("pipeline boom")
            : Task.CompletedTask);
        WebApplicationServer server = CreateServer(pipeline, new FakeHttpConnectionListener(connection));

        // Act
        await server.StartAsync();

        // Assert
        await Should.NotThrowAsync(() => connection.Disposed.Task.WaitAsync(_timeout));
        faulted.CancelCount.ShouldBe(1);
        faulted.DisposeCount.ShouldBe(1);
        connection.Context.SendCount.ShouldBe(1);
        pipeline.Executed.ShouldNotContain(next);
        connection.AbortCount.ShouldBe(0);

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: A failed HTTP/1.1 send aborts only that connection")]
    public async Task StartAsync_WhenSequentialSendFails_AbortsTheConnection()
    {
        // Arrange — the response of the first exchange cannot be written, so the connection's
        // framing is unknown and it cannot carry the second request.
        IOException sendFailure = new("wire gone");
        FakeHttpContext first = new();
        FakeHttpContext second = new();
        FakeHttpConnectionContext connectionContext = new(new[] { first, second })
        {
            SendHandler = (_, _) => ValueTask.FromException(sendFailure),
        };
        FakeHttpConnection connection = new(connectionContext);
        FakePipeline pipeline = new();
        WebApplicationServer server = CreateServer(pipeline, new FakeHttpConnectionListener(connection));

        // Act
        await server.StartAsync();

        // Assert
        await Should.NotThrowAsync(() => connection.Disposed.Task.WaitAsync(_timeout));
        connection.AbortCount.ShouldBe(1);
        connection.AbortReason.ShouldBeSameAs(sendFailure);
        first.DisposeCount.ShouldBe(1);
        pipeline.Executed.ShouldNotContain(second);
        connectionContext.DisposeCount.ShouldBe(1);

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: HTTP/1.1 exchanges are served one at a time, the next received only after the previous response is sent")]
    public async Task ServeConnection_WithSequentialExchanges_ReceivesTheNextOnlyAfterThePreviousIsSentAndDisposed()
    {
        // Arrange — every step is journaled. The pipeline yields, so a server that dispatched
        // HTTP/1.1 exchanges concurrently would ask for the second before the first was sent.
        ConcurrentQueue<string> journal = new();
        FakeHttpContext first = new() { OnDisposing = () => journal.Enqueue("dispose:first") };
        FakeHttpContext second = new() { OnDisposing = () => journal.Enqueue("dispose:second") };
        string NameOf(IHttpContext exchange) => ReferenceEquals(exchange, first) ? "first" : "second";

        FakeHttpConnectionContext connectionContext = new(new[] { first, second })
        {
            OnReceiving = exchange => journal.Enqueue($"receive:{NameOf(exchange)}"),
            SendHandler = (exchange, _) =>
            {
                journal.Enqueue($"send:{NameOf(exchange)}");
                return ValueTask.CompletedTask;
            },
        };
        FakeHttpConnection connection = new(connectionContext);
        FakePipeline pipeline = new(async (context, _) =>
        {
            journal.Enqueue($"execute:{NameOf(context)}");
            await Task.Delay(20);
        });
        WebApplicationServer server = CreateServer(pipeline, new FakeHttpConnectionListener(connection));

        // Act
        await server.StartAsync();
        await Should.NotThrowAsync(() => connection.Disposed.Task.WaitAsync(_timeout));

        // Assert
        journal.ShouldBe(new[]
        {
            "receive:first", "execute:first", "send:first", "dispose:first",
            "receive:second", "execute:second", "send:second", "dispose:second",
        });

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: Multiplexed exchanges on one connection run concurrently")]
    public async Task ServeConnection_WithMultiplexedExchanges_RunsThemConcurrently()
    {
        // Arrange — the first stream's pipeline returns only after the second stream's response has
        // been sent. Serving the connection's exchanges one at a time would park here until shutdown.
        FakeHttpContext first = new(HttpVersion.Http20);
        FakeHttpContext second = new(HttpVersion.Http20);
        TaskCompletionSource secondSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<IHttpContext> sendOrder = new();
        FakeHttpConnectionContext connectionContext = new(new[] { first, second })
        {
            SendHandler = (exchange, _) =>
            {
                sendOrder.Enqueue(exchange);

                if (ReferenceEquals(exchange, second))
                {
                    secondSent.TrySetResult();
                }

                return ValueTask.CompletedTask;
            },
        };
        FakeHttpConnection connection = new(connectionContext);
        FakePipeline pipeline = new(async (context, cancellationToken) =>
        {
            if (ReferenceEquals(context, first))
            {
                await secondSent.Task.WaitAsync(cancellationToken);
            }
        });
        WebApplicationServer server = CreateServer(pipeline, new FakeHttpConnectionListener(connection));

        // Act
        await server.StartAsync();

        // Assert — both streams are answered with their own responses, the second first.
        await Should.NotThrowAsync(() => connection.Disposed.Task.WaitAsync(_timeout));
        sendOrder.ShouldBe(new IHttpContext[] { second, first });
        first.CancelCount.ShouldBe(0);
        second.CancelCount.ShouldBe(0);
        first.DisposeCount.ShouldBe(1);
        second.DisposeCount.ShouldBe(1);
        connection.AbortCount.ShouldBe(0);

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: A faulting stream is answered with 500 while its sibling on the same connection is served")]
    public async Task ServeConnection_WhenMultiplexedPipelineThrows_AnswersOnlyThatStreamWith500()
    {
        // Arrange — the sibling stream parks until the faulted stream has been answered, so it is
        // still in flight on the same connection when the fault is handled.
        FakeHttpResponse faultedResponse = new();
        faultedResponse.Headers[HttpHeaderKey.ContentType] = "text/plain";
        await faultedResponse.Body.WriteAsync(new byte[] { 1, 2, 3 });
        FakeHttpResponse siblingResponse = new();

        FakeHttpContext faulted = new(HttpVersion.Http20, faultedResponse);
        FakeHttpContext sibling = new(HttpVersion.Http20, siblingResponse);
        TaskCompletionSource faultedAnswered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHttpConnectionContext connectionContext = new(new[] { faulted, sibling })
        {
            SendHandler = (exchange, _) =>
            {
                if (ReferenceEquals(exchange, faulted))
                {
                    faultedAnswered.TrySetResult();
                }

                return ValueTask.CompletedTask;
            },
        };
        FakeHttpConnection connection = new(connectionContext);
        FakePipeline pipeline = new(async (context, cancellationToken) =>
        {
            if (ReferenceEquals(context, faulted))
            {
                throw new InvalidOperationException("stream boom");
            }

            await faultedAnswered.Task.WaitAsync(cancellationToken);
            context.Response.StatusCode = HttpStatusCode.Accepted;
        });
        WebApplicationServer server = CreateServer(pipeline, new FakeHttpConnectionListener(connection));

        // Act
        await server.StartAsync();

        // Assert
        await Should.NotThrowAsync(() => connection.Disposed.Task.WaitAsync(_timeout));
        faultedResponse.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        faultedResponse.Headers.Count.ShouldBe(0);
        faultedResponse.Body.Length.ShouldBe(0);
        faulted.CancelCount.ShouldBe(0);
        siblingResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        sibling.CancelCount.ShouldBe(0);
        connectionContext.SendCount.ShouldBe(2);
        connection.AbortCount.ShouldBe(0);

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: A faulted stream whose response cannot be replaced is reset while its sibling is served")]
    public async Task ServeConnection_WhenMultiplexedFaultCannotBeAnswered_ResetsOnlyThatStream()
    {
        // Arrange — the faulted stream exposes no usable response, so the server resets it: it
        // cancels the exchange and hands it back to the transport, which maps that to the version's
        // per-stream reset.
        FakeHttpContext faulted = new(HttpVersion.Http20);
        FakeHttpContext sibling = new(HttpVersion.Http20);
        FakeHttpConnection connection = new(new FakeHttpConnectionContext(new[] { faulted, sibling }));
        FakePipeline pipeline = new((context, _) => ReferenceEquals(context, faulted)
            ? throw new InvalidOperationException("stream boom")
            : Task.CompletedTask);
        WebApplicationServer server = CreateServer(pipeline, new FakeHttpConnectionListener(connection));

        // Act
        await server.StartAsync();

        // Assert
        await Should.NotThrowAsync(() => connection.Disposed.Task.WaitAsync(_timeout));
        faulted.CancelCount.ShouldBe(1);
        sibling.CancelCount.ShouldBe(0);
        connection.Context.SendCount.ShouldBe(2);
        connection.AbortCount.ShouldBe(0);

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: A cancelled stream is reset without affecting its sibling")]
    public async Task ServeConnection_WhenMultiplexedExchangeIsCancelled_ResetsOnlyThatStream()
    {
        // Arrange — the exchange's own cancellation fired (a peer reset, say) and its pipeline
        // unwound with OperationCanceledException: an abandoned exchange, not a fault.
        using CancellationTokenSource peerReset = new();
        peerReset.Cancel();
        FakeHttpContext cancelled = new(HttpVersion.Http20, new FakeHttpResponse(), peerReset.Token);
        FakeHttpContext sibling = new(HttpVersion.Http20);
        FakeHttpConnection connection = new(new FakeHttpConnectionContext(new[] { cancelled, sibling }));
        FakePipeline pipeline = new((context, _) =>
        {
            context.RequestCancelled.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        WebApplicationServer server = CreateServer(pipeline, new FakeHttpConnectionListener(connection));

        // Act
        await server.StartAsync();

        // Assert — reset, not answered with a 500.
        await Should.NotThrowAsync(() => connection.Disposed.Task.WaitAsync(_timeout));
        cancelled.CancelCount.ShouldBe(1);
        cancelled.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        sibling.CancelCount.ShouldBe(0);
        connection.Context.SendCount.ShouldBe(2);
        connection.AbortCount.ShouldBe(0);

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: A failed send on one stream resets that stream and its sibling is served")]
    public async Task ServeConnection_WhenMultiplexedSendFails_ResetsOnlyThatStream()
    {
        // Arrange — the first write of the failing stream throws; the reset that follows succeeds.
        FakeHttpContext failing = new(HttpVersion.Http20);
        FakeHttpContext sibling = new(HttpVersion.Http20);
        int failingSends = 0;
        FakeHttpConnectionContext connectionContext = new(new[] { failing, sibling })
        {
            SendHandler = (exchange, _) =>
                ReferenceEquals(exchange, failing) && Interlocked.Increment(ref failingSends) == 1
                    ? ValueTask.FromException(new IOException("stream write failed"))
                    : ValueTask.CompletedTask,
        };
        FakeHttpConnection connection = new(connectionContext);
        WebApplicationServer server = CreateServer(new FakePipeline(), new FakeHttpConnectionListener(connection));

        // Act
        await server.StartAsync();

        // Assert — the failing stream was reset (cancelled, then handed back to the transport once
        // more); the sibling was answered normally; the connection was never aborted.
        await Should.NotThrowAsync(() => connection.Disposed.Task.WaitAsync(_timeout));
        failing.CancelCount.ShouldBe(1);
        Volatile.Read(ref failingSends).ShouldBe(2);
        sibling.CancelCount.ShouldBe(0);
        connectionContext.SendCount.ShouldBe(3);
        connection.AbortCount.ShouldBe(0);
        failing.DisposeCount.ShouldBe(1);
        sibling.DisposeCount.ShouldBe(1);

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: StopAsync waits for an in-flight stream before disposing its connection")]
    public async Task StopAsync_WithInFlightMultiplexedExchange_WaitsForItBeforeDisposingTheConnection()
    {
        // Arrange — the stream ignores cancellation and parks on a test-owned gate; the connection's
        // receive loop parks after yielding it, the way an HTTP/2 connection waits for its next stream.
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool contextDisposedFirst = false;
        FakeHttpConnectionContext connectionContext = null!;
        FakeHttpContext exchange = new(HttpVersion.Http20)
        {
            OnDisposing = () => contextDisposedFirst = connectionContext.DisposeCount > 0,
        };
        connectionContext = new FakeHttpConnectionContext(new[] { exchange }, parkAfterExchanges: true);
        FakeHttpConnection connection = new(connectionContext);
        FakePipeline pipeline = new(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        FakeHttpConnectionListener listener = new(connection);
        WebApplicationServer server = CreateServer(pipeline, listener);

        await server.StartAsync();
        await entered.Task.WaitAsync(_timeout);

        // Act
        Task stopTask = server.StopAsync();
        await Task.Delay(250);

        // Assert — the stop is held by the running stream, and nothing was torn down under it.
        stopTask.IsCompleted.ShouldBeFalse();
        connection.DisposeCount.ShouldBe(0);
        connectionContext.DisposeCount.ShouldBe(0);

        release.TrySetResult();
        await Should.NotThrowAsync(() => stopTask.WaitAsync(_timeout));
        exchange.DisposeCount.ShouldBe(1);
        contextDisposedFirst.ShouldBeFalse();
        connectionContext.DisposeCount.ShouldBe(1);
        connection.DisposeCount.ShouldBe(1);
        listener.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: A multiplexed connection holds its concurrency slot until its streams drain")]
    public async Task StartAsync_WithMaxConcurrentConnections_HoldsTheSlotUntilInFlightStreamsDrain()
    {
        // Arrange — cap of 1. Connection A's receive sequence ends right after it yields one stream,
        // which then parks; connection B must stay in the backlog until that stream finishes.
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHttpContext streamA = new(HttpVersion.Http20);
        FakeHttpConnection connectionA = new(new FakeHttpConnectionContext(new[] { streamA }));

        FakeHttpContext exchangeB = new();
        FakeHttpConnection connectionB = new(new FakeHttpConnectionContext(new[] { exchangeB }));

        TaskCompletionSource exchangeBProcessed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakePipeline pipeline = new(async (context, _) =>
        {
            if (ReferenceEquals(context, streamA))
            {
                await release.Task;
            }

            if (ReferenceEquals(context, exchangeB))
            {
                exchangeBProcessed.TrySetResult();
            }
        });

        WebApplicationServer server = CreateServer(
            pipeline,
            new FakeHttpConnectionListener(connectionA, connectionB),
            maxConcurrentConnections: 1);

        // Act
        await server.StartAsync();
        await Should.NotThrowAsync(() => WaitForAsync(() => pipeline.Executed.Contains(streamA), _timeout));

        // Assert — A's receive loop is done, but its stream still holds the slot.
        await Task.Delay(250);
        connectionB.OpenCount.ShouldBe(0);
        connectionA.DisposeCount.ShouldBe(0);

        release.TrySetResult();

        await Should.NotThrowAsync(() => exchangeBProcessed.Task.WaitAsync(_timeout));
        connectionA.DisposeCount.ShouldBe(1);
        connectionB.OpenCount.ShouldBe(1);

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
        await Should.NotThrowAsync(() => connection.Disposed.Task.WaitAsync(_timeout));

        // Assert
        connection.DisposeCount.ShouldBe(1);
        connection.Context.DisposeCount.ShouldBe(1);
        connection.AbortCount.ShouldBe(0);
        exchange.DisposeCount.ShouldBe(1);
        connection.Context.SendCount.ShouldBe(1);

        await server.StopAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: Response completion callbacks run only after the response write completes")]
    public async Task ServeConnection_WithResponseCompletionCallback_RunsCallbackAfterSendCompletes()
    {
        // Arrange
        TaskCompletionSource sendEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource completionInvoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHttpContext exchange = new();
        FakeHttpConnectionContext connectionContext = new(new[] { exchange })
        {
            SendHandler = async (_, cancellationToken) =>
            {
                sendEntered.TrySetResult();
                await releaseSend.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            },
        };
        FakeHttpConnection connection = new(connectionContext);
        FakePipeline pipeline = new((context, _) =>
        {
            ResponseCompletionFeature feature =
                context.Features.Get<ResponseCompletionFeature>().ShouldNotBeNull();
            feature.Register(() =>
            {
                completionInvoked.TrySetResult();
                return ValueTask.CompletedTask;
            });
            return Task.CompletedTask;
        });
        WebApplicationServer server = CreateServer(
            pipeline,
            new FakeHttpConnectionListener(connection));

        // Act
        await server.StartAsync();
        await sendEntered.Task.WaitAsync(_timeout);

        // Assert
        completionInvoked.Task.IsCompleted.ShouldBeFalse();

        releaseSend.TrySetResult();
        await completionInvoked.Task.WaitAsync(_timeout);
        await connection.Disposed.Task.WaitAsync(_timeout);

        connectionContext.SendCount.ShouldBe(1);
        exchange.DisposeCount.ShouldBe(1);

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
        await Should.NotThrowAsync(() => connection.Disposed.Task.WaitAsync(_timeout));

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
        await Should.NotThrowAsync(() => exchangeProcessed.Task.WaitAsync(_timeout));

        // Act — the parked connection must be drained by the stop, not hang it.
        await Should.NotThrowAsync(() => server.StopAsync().WaitAsync(_timeout));

        // Assert — graceful drain: connection disposed, listener disposed, no escaped exception.
        connection.DisposeCount.ShouldBe(1);
        connection.Context.DisposeCount.ShouldBe(1);
        listener.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Server: StopAsync before StartAsync is a no-op")]
    public async Task StopAsync_WithoutStart_DoesNotThrow()
    {
        WebApplicationServer server = CreateServer(new FakePipeline(), new FakeHttpConnectionListener());

        await Should.NotThrowAsync(() => server.StopAsync().WaitAsync(_timeout));
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
        await Should.NotThrowAsync(() => WaitForAsync(() => connectionA.OpenCount == 1, _timeout));

        // Assert — B stays in the backlog: not opened while the slot is held.
        await Task.Delay(250);
        connectionB.OpenCount.ShouldBe(0);
        exchangeBProcessed.Task.IsCompleted.ShouldBeFalse();

        // Act — free the slot; B is now accepted, opened, and served.
        releaseA.TrySetResult();

        // Assert
        await Should.NotThrowAsync(() => exchangeBProcessed.Task.WaitAsync(_timeout));
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

    [Theory(DisplayName = "Cohesion Test [Web Hosting] - Server: A response the transport refuses for a field it cannot carry is answered with 500 and the connection keeps serving")]
    [InlineData(HttpVersion.Http11)]
    [InlineData(HttpVersion.Http20)]
    public async Task ServeConnection_WhenTransportRefusesResponseField_AnswersWith500(HttpVersion version)
    {
        // Arrange — the application reflected request text with a CRLF into a header and returned. The
        // transport refuses that head before writing it (#1183), which leaves the response unstarted.
        FakeHttpResponse refusedResponse = new();
        FakeHttpContext refused = new(version, refusedResponse);
        FakeHttpContext next = new(version);
        bool completionInvoked = false;
        int refusedSends = 0;
        FakeHttpConnectionContext connectionContext = new(new[] { refused, next })
        {
            SendHandler = (exchange, _) =>
            {
                if (ReferenceEquals(exchange, refused) && refusedResponse.Headers.ContainsKey(new HttpHeaderKey("x-echo")))
                {
                    refusedSends++;
                    return ValueTask.FromException(new RefusedResponseFieldException());
                }

                return ValueTask.CompletedTask;
            },
        };
        FakeHttpConnection connection = new(connectionContext);
        FakePipeline pipeline = new((context, _) =>
        {
            if (ReferenceEquals(context, refused))
            {
                context.Features.Get<ResponseCompletionFeature>().ShouldNotBeNull().Register(() =>
                {
                    completionInvoked = true;
                    return ValueTask.CompletedTask;
                });
                context.Response.Headers[new HttpHeaderKey("x-echo")] = "a\r\nSet-Cookie: injected=1";
                context.Response.StatusCode = HttpStatusCode.Found;
            }

            return Task.CompletedTask;
        });
        WebApplicationServer server = CreateServer(pipeline, new FakeHttpConnectionListener(connection));

        // Act
        await server.StartAsync();

        // Assert — the refused response is replaced with a bare 500, as a pipeline fault would be: its
        // completion callbacks do not run, nothing is reset, and the connection serves the next exchange.
        await Should.NotThrowAsync(() => connection.Disposed.Task.WaitAsync(_timeout));
        refusedSends.ShouldBe(1);
        refusedResponse.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        refusedResponse.Headers.Count.ShouldBe(0);
        refused.CancelCount.ShouldBe(0);
        completionInvoked.ShouldBeFalse();
        pipeline.Executed.ShouldContain(next);
        connectionContext.SendCount.ShouldBe(3);
        connection.AbortCount.ShouldBe(0);

        await server.StopAsync();
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

    /// <summary>What a transport throws when it refuses a response field before writing the head.</summary>
    private sealed class RefusedResponseFieldException : HttpException
    {
        public RefusedResponseFieldException()
            : base("A response field cannot be sent.")
        {
            Code = HttpErrorCode.InvalidResponseField;
        }
    }
}
