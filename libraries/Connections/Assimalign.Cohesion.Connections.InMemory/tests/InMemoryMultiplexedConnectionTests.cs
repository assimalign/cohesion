using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.InMemory.Tests;

public class InMemoryMultiplexedConnectionTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(5);

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed BindAsync: Logical bind should complete until listener disposal")]
    public async Task BindAsync_BeforeAndAfterDispose_ShouldRespectTerminalDisposal()
    {
        // Arrange
        InMemoryMultiplexedConnectionListener listener = new();

        // Act
        await listener.BindAsync();
        await listener.DisposeAsync();

        // Assert
        await Should.ThrowAsync<ObjectDisposedException>(async () => await listener.BindAsync());
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed: Opening a stream should be accepted by the peer and round-trip")]
    public async Task OpenStream_ShouldBeAcceptedByPeerAndRoundTrip()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();

        // Act
        Connection clientStream = await client.OpenStreamAsync(cancellationToken: cancellation.Token);
        Connection serverStream = await server.AcceptStreamAsync(cancellation.Token);

        byte[] payload = [4, 5, 6];
        await clientStream.Output.WriteAsync(payload, cancellation.Token);
        byte[] received = await serverStream.Input.ReadExactlyAsync(payload.Length, cancellation.Token);

        // Assert
        received.ShouldBe(payload);
        clientStream.Capabilities.IsMultiplexed.ShouldBeFalse();
        client.Capabilities.IsMultiplexed.ShouldBeTrue();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed: Multiple streams should be independently accepted")]
    public async Task OpenStream_MultipleStreams_ShouldBeIndependentlyAccepted()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();

        // Act
        Connection first = await client.OpenStreamAsync(cancellationToken: cancellation.Token);
        Connection second = await client.OpenStreamAsync(cancellationToken: cancellation.Token);

        Connection serverFirst = await server.AcceptStreamAsync(cancellation.Token);
        Connection serverSecond = await server.AcceptStreamAsync(cancellation.Token);

        // Assert
        first.Id.ShouldNotBe(second.Id);
        serverFirst.Id.ShouldNotBe(serverSecond.Id);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed: An outbound unidirectional stream should be write-only for the opener and read-only for the peer")]
    public async Task OpenStream_WriteOnly_ShouldMirrorAsReadOnlyOnPeer()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();

        // Act
        Connection clientStream = await client.OpenStreamAsync(ConnectionDirection.WriteOnly, cancellation.Token);
        Connection serverStream = await server.AcceptStreamAsync(cancellation.Token);

        byte[] payload = [1, 1, 2, 3, 5];
        await clientStream.Output.WriteAsync(payload, cancellation.Token);
        byte[] received = await serverStream.Input.ReadExactlyAsync(payload.Length, cancellation.Token);

        // Assert
        clientStream.Direction.ShouldBe(ConnectionDirection.WriteOnly);
        serverStream.Direction.ShouldBe(ConnectionDirection.ReadOnly);
        received.ShouldBe(payload);

        // The read-only peer end must reject writes.
        Should.Throw<InvalidOperationException>(() => serverStream.Output.GetMemory());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed: Opening a read-only stream should throw")]
    public async Task OpenStream_ReadOnly_ShouldThrowArgumentException()
    {
        // Arrange
        (MultiplexedConnection client, MultiplexedConnection _) = InMemoryMultiplexedConnectionPair.Create();

        // Act & Assert
        await Should.ThrowAsync<ArgumentException>(
            async () => await client.OpenStreamAsync(ConnectionDirection.ReadOnly));

        await client.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed: Accepting after the connection closes should throw OperationCanceled")]
    public async Task AcceptStream_AfterDispose_ShouldThrowOperationCanceled()
    {
        // Arrange
        (MultiplexedConnection client, MultiplexedConnection _) = InMemoryMultiplexedConnectionPair.Create();

        // Act
        await client.DisposeAsync();

        // Assert
        client.State.ShouldBe(ConnectionState.Closed);
        await Should.ThrowAsync<OperationCanceledException>(async () => await client.AcceptStreamAsync());
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed: Listener dial then accept should yield a connected multiplexed pair")]
    public async Task Dial_ThenAccept_ShouldYieldMultiplexedPair()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using InMemoryMultiplexedConnectionListener listener = new();
        InMemoryMultiplexedConnectionFactory factory = listener.CreateFactory();

        // Act
        ValueTask<MultiplexedConnection> acceptTask = listener.AcceptAsync(cancellation.Token);

        MultiplexedConnection client = await factory.ConnectAsync(listener.EndPoint, cancellation.Token);
        MultiplexedConnection server = await acceptTask;

        Connection clientStream = await client.OpenStreamAsync(cancellationToken: cancellation.Token);
        Connection serverStream = await server.AcceptStreamAsync(cancellation.Token);

        byte[] payload = [42];
        await clientStream.Output.WriteAsync(payload, cancellation.Token);
        byte[] received = await serverStream.Input.ReadExactlyAsync(1, cancellation.Token);

        // Assert
        received.ShouldBe(payload);
        client.Capabilities.IsMultiplexed.ShouldBeTrue();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    /// <summary>How the peer end of a stream abandons it.</summary>
    public enum StreamAbort
    {
        /// <summary>Aborts the stream: both directions.</summary>
        Abort,

        /// <summary>Completes its output with an error: the in-memory RESET_STREAM.</summary>
        ResetSending,

        /// <summary>Completes its input with an error: the in-memory STOP_SENDING.</summary>
        StopReceiving,
    }

    [Theory(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed: A peer abandoning a stream should signal the other end's ConnectionClosed")]
    [InlineData(StreamAbort.Abort)]
    [InlineData(StreamAbort.ResetSending)]
    [InlineData(StreamAbort.StopReceiving)]
    public async Task StreamConnectionClosed_OnPeerAbandoningTheStream_ShouldFire(StreamAbort abort)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        Connection clientStream = await client.OpenStreamAsync(cancellationToken: cancellation.Token);
        Connection serverStream = await server.AcceptStreamAsync(cancellation.Token);
        ConnectionAbortedException reason = new("The peer abandoned the stream.");

        // Act
        switch (abort)
        {
            case StreamAbort.Abort:
                clientStream.Abort(reason);
                break;

            case StreamAbort.ResetSending:
                clientStream.Output.Complete(reason);
                break;

            case StreamAbort.StopReceiving:
                clientStream.Input.Complete(reason);
                break;
        }

        // Assert — the other end learns of it without reading or writing; its own state is unchanged.
        serverStream.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        serverStream.State.ShouldBe(ConnectionState.Open);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed AbortRead: The peer's writes should fail with the caller's code")]
    public async Task AbortRead_WithErrorCode_ShouldFailThePeersWritesWithTheCode()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        Connection clientStream = await client.OpenStreamAsync(cancellationToken: cancellation.Token);
        Connection serverStream = await server.AcceptStreamAsync(cancellation.Token);

        // Act — the in-memory STOP_SENDING.
        serverStream.ShouldBeAssignableTo<IMultiplexedStreamAbort>()!.AbortRead(0x100);

        // Assert — the client learns of it at once and its next write carries the code; the server's own
        // stream keeps its lifecycle and can still send.
        ConnectionResetException reset = await Should.ThrowAsync<ConnectionResetException>(
            async () => await clientStream.Output.WriteAsync(new byte[] { 1 }, cancellation.Token));

        reset.ApplicationErrorCode.ShouldBe(0x100);
        clientStream.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        serverStream.State.ShouldBe(ConnectionState.Open);
        serverStream.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();

        await serverStream.Output.WriteAsync(new byte[] { 7 }, cancellation.Token);
        (await clientStream.Input.ReadExactlyAsync(1, cancellation.Token)).ShouldBe(new byte[] { 7 });

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed AbortWrite: The peer's reads should fail with the caller's code")]
    public async Task AbortWrite_WithErrorCode_ShouldFailThePeersReadsWithTheCode()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        Connection clientStream = await client.OpenStreamAsync(cancellationToken: cancellation.Token);
        Connection serverStream = await server.AcceptStreamAsync(cancellation.Token);
        ValueTask<System.IO.Pipelines.ReadResult> pendingRead = clientStream.Input.ReadAsync(cancellation.Token);

        // Act — the in-memory RESET_STREAM.
        serverStream.ShouldBeAssignableTo<IMultiplexedStreamAbort>()!.AbortWrite(0x10e);

        // Assert — the client's read in flight fails with the code.
        ConnectionResetException reset = await Should.ThrowAsync<ConnectionResetException>(async () => await pendingRead);

        reset.ApplicationErrorCode.ShouldBe(0x10e);
        clientStream.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        serverStream.State.ShouldBe(ConnectionState.Open);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed AbortRead: A read in flight on the aborting end should fail instead of hanging")]
    public async Task AbortRead_WithReadInFlight_ShouldFailTheRead()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        Connection clientStream = await client.OpenStreamAsync(cancellationToken: cancellation.Token);
        Connection serverStream = await server.AcceptStreamAsync(cancellation.Token);
        Task<System.IO.Pipelines.ReadResult> pendingRead = serverStream.Input.ReadAsync(cancellation.Token).AsTask();

        // Act
        serverStream.ShouldBeAssignableTo<IMultiplexedStreamAbort>()!.AbortRead(0x100);

        // Assert — the waiting read and every later one fail; the client never had to send or end anything.
        await Should.ThrowAsync<ConnectionAbortedException>(() => pendingRead.WaitAsync(_testTimeout));
        await Should.ThrowAsync<ConnectionAbortedException>(async () => await serverStream.Input.ReadAsync(cancellation.Token));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed abort: A stream later aborted should keep the codes its directions were aborted with")]
    public async Task Abort_AfterAbortingBothDirections_ShouldKeepTheCodes()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        Connection clientStream = await client.OpenStreamAsync(cancellationToken: cancellation.Token);
        Connection serverStream = await server.AcceptStreamAsync(cancellation.Token);

        // Act — a reset with a code, then the lifecycle abort with a reason of its own.
        IMultiplexedStreamAbort abort = serverStream.ShouldBeAssignableTo<IMultiplexedStreamAbort>()!;
        abort.AbortWrite(0x10b);
        abort.AbortRead(0x10b);
        serverStream.Abort(new InvalidOperationException("the reason stays local"));

        // Assert
        ConnectionResetException read = await Should.ThrowAsync<ConnectionResetException>(
            async () => await clientStream.Input.ReadAsync(cancellation.Token));
        ConnectionResetException write = await Should.ThrowAsync<ConnectionResetException>(
            async () => await clientStream.Output.WriteAsync(new byte[] { 1 }, cancellation.Token));

        read.ApplicationErrorCode.ShouldBe(0x10b);
        write.ApplicationErrorCode.ShouldBe(0x10b);
        serverStream.State.ShouldBe(ConnectionState.Aborted);
        serverStream.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed abort: A direction a unidirectional stream lacks should not be signaled")]
    public async Task AbortReadAndWrite_OnMissingDirection_ShouldHaveNoEffect()
    {
        // Arrange — the opener writes only; the peer reads only.
        using CancellationTokenSource cancellation = new(_testTimeout);
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        Connection writeOnly = await client.OpenStreamAsync(ConnectionDirection.WriteOnly, cancellation.Token);
        Connection readOnly = await server.AcceptStreamAsync(cancellation.Token);

        // Act
        writeOnly.ShouldBeAssignableTo<IMultiplexedStreamAbort>()!.AbortRead(0x100);
        readOnly.ShouldBeAssignableTo<IMultiplexedStreamAbort>()!.AbortWrite(0x100);

        // Assert — neither end was told the other abandoned the stream, and the data still flows.
        writeOnly.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
        readOnly.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();

        await writeOnly.Output.WriteAsync(new byte[] { 3 }, cancellation.Token);
        (await readOnly.Input.ReadExactlyAsync(1, cancellation.Token)).ShouldBe(new byte[] { 3 });

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Theory(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed abort: A code outside the QUIC range should be rejected")]
    [InlineData(-1L)]
    [InlineData(1L << 62)]
    public async Task AbortReadAndWrite_WithCodeOutOfRange_ShouldThrowArgumentOutOfRange(long errorCode)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        Connection clientStream = await client.OpenStreamAsync(cancellationToken: cancellation.Token);
        IMultiplexedStreamAbort abort = clientStream.ShouldBeAssignableTo<IMultiplexedStreamAbort>()!;

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => abort.AbortRead(errorCode));
        Should.Throw<ArgumentOutOfRangeException>(() => abort.AbortWrite(errorCode));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Byte-stream pair: Should not offer a code-carrying abort")]
    public void ConnectionPair_OnByteStreamPair_ShouldNotImplementStreamAbort()
    {
        // Arrange / Act
        (Connection client, Connection server) = InMemoryConnectionPair.Create();

        // Assert — a single-stream transport has no per-direction code to carry.
        client.ShouldNotBeAssignableTo<IMultiplexedStreamAbort>();
        server.ShouldNotBeAssignableTo<IMultiplexedStreamAbort>();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Multiplexed: A peer ending its side of a stream should not signal the other end's ConnectionClosed")]
    public async Task StreamConnectionClosed_OnPeerGracefulHalfClose_ShouldNotFire()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        (MultiplexedConnection client, MultiplexedConnection server) = InMemoryMultiplexedConnectionPair.Create();
        Connection clientStream = await client.OpenStreamAsync(cancellationToken: cancellation.Token);
        Connection serverStream = await server.AcceptStreamAsync(cancellation.Token);

        // Act — the FIN.
        await clientStream.Output.CompleteAsync();

        // Assert
        serverStream.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
