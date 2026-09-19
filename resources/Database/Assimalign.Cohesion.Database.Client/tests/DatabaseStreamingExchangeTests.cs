using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client.Tests;

/// <summary>A non-Blob model uses shared streaming and pool ownership without implementing either.</summary>
public sealed class DatabaseStreamingExchangeTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Client] - Streaming: live streams reserve their rental through verified EOF until disposal")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteStreamingAsync_CompleteTransfer_ShouldReturnCleanRentalOnDispose(bool synchronous)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = new StreamingClientTestHarness();
        var original = await harness.Client.RentAsync(timeout.Token);
        await original.DisposeAsync();

        // Act: metadata makes a live stream available while the peer is still gated mid-response.
        await using var stream = await harness.Client.ExecuteStreamingAsync(StreamingClientTestHarness.CreateExchange(), timeout.Token);
        await harness.FirstChunkSent.Task.WaitAsync(timeout.Token);
        Task<IDatabaseConnection> pending = harness.Client.RentAsync(timeout.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        harness.TransferSent.Task.IsCompleted.ShouldBeFalse();
        stream.CanRead.ShouldBeTrue();
        stream.CanSeek.ShouldBeFalse();
        stream.CanWrite.ShouldBeFalse();
        harness.ContinueResponse.TrySetResult();
        using var output = new MemoryStream();
        if (synchronous)
        {
            await Task.Run(() => stream.CopyTo(output), timeout.Token);
        }
        else
        {
            await stream.CopyToAsync(output, timeout.Token);
        }

        // Assert: EOF has been verified, but the caller still owns the rental until disposal.
        output.ToArray().ShouldBe(harness.Payload);
        (await stream.ReadAsync(new byte[1], timeout.Token)).ShouldBe(0);
        pending.IsCompleted.ShouldBeFalse();
        await stream.DisposeAsync();
        await using var next = await pending;
        next.ShouldBeSameAs(original);
        harness.AcceptedConnections.ShouldBe(1);
        (await next.ExecuteAsync(StreamingClientTestHarness.CreatePing(), timeout.Token)).ShouldBe(ProtocolMessageType.Pong);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Streaming: early disposal discards the transport before admitting the next rental")]
    public async Task ExecuteStreamingAsync_DisposedMidTransfer_ShouldRentFreshTransport()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = new StreamingClientTestHarness();
        await using var stream = await harness.Client.ExecuteStreamingAsync(StreamingClientTestHarness.CreateExchange(), timeout.Token);
        (await stream.ReadAsync(new byte[1024], timeout.Token)).ShouldBeGreaterThan(0);
        Task<IDatabaseConnection> pending = harness.Client.RentAsync(timeout.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();

        // Act
        await stream.DisposeAsync();
        await using var next = await pending;

        // Assert
        harness.AcceptedConnections.ShouldBe(2);
        (await next.ExecuteAsync(StreamingClientTestHarness.CreatePing(), timeout.Token)).ShouldBe(ProtocolMessageType.Pong);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Streaming: the opening token remains active and releases a canceled transfer without requiring disposal")]
    public async Task ExecuteStreamingAsync_RequestCanceledMidTransfer_ShouldDiscardAndReleaseRental()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var harness = new StreamingClientTestHarness();
        await using var stream = await harness.Client.ExecuteStreamingAsync(StreamingClientTestHarness.CreateExchange(), request.Token);
        await stream.ReadExactlyAsync(new byte[StreamingClientTestHarness.ChunkLength], timeout.Token);
        Task<IDatabaseConnection> pending = harness.Client.RentAsync(timeout.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();

        // Act: cancellation must release the pool even if the caller never reads or disposes again.
        request.Cancel();
        await using var next = await pending;

        // Assert
        harness.AcceptedConnections.ShouldBe(2);
        await Should.ThrowAsync<OperationCanceledException>(async () => { _ = await stream.ReadAsync(new byte[1], timeout.Token); });
        (await next.ExecuteAsync(StreamingClientTestHarness.CreatePing(), timeout.Token)).ShouldBe(ProtocolMessageType.Pong);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Streaming: canceling a read aborts the exchange and releases a broken rental")]
    public async Task ExecuteStreamingAsync_ReadCanceledMidTransfer_ShouldDiscardAndReleaseRental()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var readCancellation = new CancellationTokenSource();
        await using var harness = new StreamingClientTestHarness();
        await using var stream = await harness.Client.ExecuteStreamingAsync(StreamingClientTestHarness.CreateExchange(), timeout.Token);
        await stream.ReadExactlyAsync(new byte[StreamingClientTestHarness.ChunkLength], timeout.Token);
        Task<int> read = stream.ReadAsync(new byte[1], readCancellation.Token).AsTask();
        read.IsCompleted.ShouldBeFalse();
        Task<IDatabaseConnection> pending = harness.Client.RentAsync(timeout.Token).AsTask();

        // Act
        readCancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await read);
        await using var next = await pending;

        // Assert
        harness.AcceptedConnections.ShouldBe(2);
        (await next.ExecuteAsync(StreamingClientTestHarness.CreatePing(), timeout.Token)).ShouldBe(ProtocolMessageType.Pong);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Client] - Streaming: errors and invalid completions stay failures and cannot return a dirty transport")]
    [InlineData("error")]
    [InlineData("wrong-length")]
    [InlineData("truncated")]
    public async Task ExecuteStreamingAsync_FailureAfterContent_ShouldPreserveDiagnosticsAndDiscardRental(string response)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = new StreamingClientTestHarness(response);
        await using var stream = await harness.Client.ExecuteStreamingAsync(StreamingClientTestHarness.CreateExchange(), timeout.Token);
        await stream.ReadExactlyAsync(new byte[StreamingClientTestHarness.ChunkLength], timeout.Token);
        Task<IDatabaseConnection> pending = harness.Client.RentAsync(timeout.Token).AsTask();

        // Act
        harness.ContinueResponse.TrySetResult();
        var exception = await Should.ThrowAsync<DatabaseClientException>(async () => { _ = await stream.ReadAsync(new byte[1], timeout.Token); });
        await using var next = await pending;

        // Assert: the shared Error is never translated into a model exception to control pool health.
        if (response == "error")
        {
            exception.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            exception.Message.ShouldBe("Document transfer failed after a storage error.");
        }
        await Should.ThrowAsync<DatabaseClientException>(async () => { _ = await stream.ReadAsync(new byte[1], timeout.Token); });
        harness.AcceptedConnections.ShouldBe(2);
        (await next.ExecuteAsync(StreamingClientTestHarness.CreatePing(), timeout.Token)).ShouldBe(ProtocolMessageType.Pong);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Streaming: a failure before metadata preserves the wire code and releases the rental")]
    public async Task ExecuteStreamingAsync_ErrorBeforeMetadata_ShouldPreserveCodeAndReleaseRental()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = new StreamingClientTestHarness("header-error");

        // Act
        var exception = await Should.ThrowAsync<DatabaseClientException>(async () =>
            await harness.Client.ExecuteStreamingAsync(StreamingClientTestHarness.CreateExchange(), timeout.Token));
        await using var next = await harness.Client.RentAsync(timeout.Token);

        // Assert
        exception.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        exception.Message.ShouldBe("Document transfer failed after a storage error.");
        harness.AcceptedConnections.ShouldBe(2);
        (await next.ExecuteAsync(StreamingClientTestHarness.CreatePing(), timeout.Token)).ShouldBe(ProtocolMessageType.Pong);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Streaming: concurrent disposal returns exactly one lease and cannot close its next owner")]
    public async Task ExecuteStreamingAsync_ConcurrentDisposal_ShouldReturnExactlyOnce()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = new StreamingClientTestHarness();
        await using var stream = await harness.Client.ExecuteStreamingAsync(StreamingClientTestHarness.CreateExchange(), timeout.Token);
        harness.ContinueResponse.TrySetResult();
        await stream.CopyToAsync(Stream.Null, timeout.Token);
        Task<IDatabaseConnection> pending = harness.Client.RentAsync(timeout.Token).AsTask();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] disposing = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            await start.Task.WaitAsync(timeout.Token);
            await stream.DisposeAsync();
        }, timeout.Token)).ToArray();

        // Act
        start.TrySetResult();
        await using var next = await pending;
        await Task.WhenAll(disposing);
        stream.Dispose();

        // Assert: no duplicate return may admit another renter or shut down the new owner's session.
        using var excessCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        Task<IDatabaseConnection> excess = harness.Client.RentAsync(excessCancellation.Token).AsTask();
        excess.IsCompleted.ShouldBeFalse();
        excessCancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await excess);
        harness.AcceptedConnections.ShouldBe(1);
        (await next.ExecuteAsync(StreamingClientTestHarness.CreatePing(), timeout.Token)).ShouldBe(ProtocolMessageType.Pong);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Streaming: a connection-owned stream reserves the active exchange and retains a clean connection")]
    public async Task ExecuteStreamingAsync_ConnectionOwnedTransfer_ShouldKeepLeaseAndRejectOverlap()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = new StreamingClientTestHarness();
        var original = await harness.Client.RentAsync(timeout.Token);
        await using var stream = await original.ExecuteStreamingAsync(StreamingClientTestHarness.CreateExchange(), timeout.Token);

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await original.ExecuteAsync(StreamingClientTestHarness.CreatePing(), timeout.Token));
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await original.ExecuteStreamingAsync(StreamingClientTestHarness.CreateExchange(), timeout.Token));
        harness.ContinueResponse.TrySetResult();
        await stream.CopyToAsync(Stream.Null, timeout.Token);
        await stream.DisposeAsync();
        (await original.ExecuteAsync(StreamingClientTestHarness.CreatePing(), timeout.Token)).ShouldBe(ProtocolMessageType.Pong);
        Task<IDatabaseConnection> pending = harness.Client.RentAsync(timeout.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        await original.DisposeAsync();
        await using var next = await pending;
        next.ShouldBeSameAs(original);
        harness.AcceptedConnections.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Client] - Streaming: disposing the owning connection cancels its unfinished stream before releasing the rental")]
    public async Task ExecuteStreamingAsync_ConnectionDisposedMidTransfer_ShouldDiscardRental()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = new StreamingClientTestHarness();
        var original = await harness.Client.RentAsync(timeout.Token);
        await using var stream = await original.ExecuteStreamingAsync(StreamingClientTestHarness.CreateExchange(), timeout.Token);
        (await stream.ReadAsync(new byte[1], timeout.Token)).ShouldBe(1);
        Task<IDatabaseConnection> pending = harness.Client.RentAsync(timeout.Token).AsTask();

        // Act
        await original.DisposeAsync();
        await using var next = await pending;

        // Assert
        next.ShouldNotBeSameAs(original);
        harness.AcceptedConnections.ShouldBe(2);
        (await next.ExecuteAsync(StreamingClientTestHarness.CreatePing(), timeout.Token)).ShouldBe(ProtocolMessageType.Pong);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Client] - Streaming: large writes apply backpressure and connection disposal cancels writes without model cancellation plumbing")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteStreamingAsync_LargeUntokenedWrite_ShouldStayBoundedAndCancelOnConnectionDisposal(bool synchronous)
    {
        // Arrange: the model writes one 768 KiB frame, synchronously or without an explicit token.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = new StreamingClientTestHarness("large-frame");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = await harness.Client.RentAsync(timeout.Token);
        var exchange = StreamingClientTestHarness.CreateUntokenedExchange(synchronous, started, completed);
        await using var stream = await original.ExecuteStreamingAsync(exchange, timeout.Token);
        await started.Task.WaitAsync(timeout.Token);
        (await stream.ReadAsync(new byte[1], timeout.Token)).ShouldBe(1);

        // Act: after one byte, stop consuming; the large producer write must remain bounded.
        completed.Task.IsCompleted.ShouldBeFalse();
        Task<IDatabaseConnection> pending = harness.Client.RentAsync(timeout.Token).AsTask();
        await original.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        await using var next = await pending;

        // Assert: shared lifetime cancellation interrupts even an untokened destination write.
        completed.Task.IsCompleted.ShouldBeFalse();
        next.ShouldNotBeSameAs(original);
        harness.AcceptedConnections.ShouldBe(2);
        (await next.ExecuteAsync(StreamingClientTestHarness.CreatePing(), timeout.Token)).ShouldBe(ProtocolMessageType.Pong);
    }
}
