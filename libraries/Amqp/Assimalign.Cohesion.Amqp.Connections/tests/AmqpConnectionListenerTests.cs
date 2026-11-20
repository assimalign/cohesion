using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

public class AmqpConnectionListenerTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(5);

    // Long enough that a stalled handshake is still pending when the test ends.
    private static readonly TimeSpan _stalledHandshakeTimeout = TimeSpan.FromMinutes(5);

    // Short enough that a stalled handshake times out well within the test timeout.
    private static readonly TimeSpan _shortHandshakeTimeout = TimeSpan.FromMilliseconds(200);

    private static AmqpConnectionListenerOptions ManualNegotiationOptions => new()
    {
        AutoNegotiateProtocolHeader = false
    };

    private static async Task<TestConnection> CreateNegotiatingCarrierAsync(CancellationToken cancellationToken)
    {
        TestConnection carrier = new();
        await carrier.WritePeerAsync(AmqpTestWire.Amqp10Header, cancellationToken);

        return carrier;
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should return a negotiated, tracked connection over a single-stream carrier")]
    public async Task AcceptAsync_OnSingleStreamCarrier_ShouldReturnNegotiatedTrackedConnection()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = await CreateNegotiatingCarrierAsync(timeout.Token);
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener);

        // Act
        await using IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);
        byte[] sentToPeer = await carrier.ReadBufferedPeerBytesAsync(timeout.Token);

        // Assert
        connection.Id.ShouldBe(carrier.Id);
        connection.State.ShouldBe(ConnectionState.Open);
        connection.LocalProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        connection.RemoteProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        sentToPeer.ShouldBe(AmqpTestWire.Amqp10Header);
        amqp.Connections.ShouldHaveSingleItem().ShouldBeSameAs(connection);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should return the connection before any bytes when auto-negotiation is disabled")]
    public async Task AcceptAsync_WithoutAutoNegotiation_ShouldReturnConnectionBeforeAnyBytes()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener, ManualNegotiationOptions);

        // Act
        await using IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);

        // Assert
        connection.Id.ShouldBe(carrier.Id);
        connection.LocalProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        connection.RemoteProtocolHeader.ShouldBeNull();
        carrier.HasBufferedPeerBytes.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should wire the connection to the carrier connection's pipes")]
    public async Task AcceptAsync_WithoutAutoNegotiation_ShouldWireConnectionToCarrierPipes()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener, ManualNegotiationOptions);
        await using IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);

        // Act
        await connection.Output.WriteAsync(new byte[] { 0x01, 0x02, 0x03 }, timeout.Token);
        byte[] observedByPeer = await carrier.ReadBufferedPeerBytesAsync(timeout.Token);

        await carrier.WritePeerAsync(new byte[] { 0x0a, 0x0b }, timeout.Token);
        ReadResult inbound = await connection.Input.ReadAsync(timeout.Token);
        byte[] observedByConnection = inbound.Buffer.ToArray();
        connection.Input.AdvanceTo(inbound.Buffer.End);

        // Assert
        observedByPeer.ShouldBe(new byte[] { 0x01, 0x02, 0x03 });
        observedByConnection.ShouldBe(new byte[] { 0x0a, 0x0b });
        connection.LocalEndPoint.ShouldBeSameAs(carrier.LocalEndPoint);
        connection.RemoteEndPoint.ShouldBeSameAs(carrier.RemoteEndPoint);
        connection.Direction.ShouldBe(ConnectionDirection.Bidirectional);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should accept the AMQP stream from the peer on a multiplexed carrier")]
    public async Task AcceptAsync_OnMultiplexedCarrier_ShouldAcceptAmqpStreamFromPeer()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection stream = await CreateNegotiatingCarrierAsync(timeout.Token);
        TestMultiplexedConnection carrier = new();
        carrier.Enqueue(stream);
        TestMultiplexedConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener);

        // Act
        await using IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);
        byte[] sentOnStream = await stream.ReadBufferedPeerBytesAsync(timeout.Token);

        // Assert
        connection.Id.ShouldBe(carrier.Id);
        connection.LocalEndPoint.ShouldBeSameAs(carrier.LocalEndPoint);
        connection.RemoteEndPoint.ShouldBeSameAs(carrier.RemoteEndPoint);
        connection.RemoteProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        carrier.AcceptStreamCount.ShouldBe(1);
        carrier.OpenStreamCount.ShouldBe(0);
        sentOnStream.ShouldBe(AmqpTestWire.Amqp10Header);
        amqp.Connections.ShouldHaveSingleItem().ShouldBeSameAs(connection);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should dispose a carrier whose handshake fails and return the next connection")]
    public async Task AcceptAsync_OnFailedHandshake_ShouldDisposeCarrierAndReturnNextConnection()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection rejected = new();
        await rejected.WritePeerAsync(AmqpTestWire.NotAProtocolHeader, timeout.Token);
        TestConnection accepted = await CreateNegotiatingCarrierAsync(timeout.Token);
        TestConnectionListener listener = new();
        listener.Enqueue(rejected);
        listener.Enqueue(accepted);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener);

        // Act
        await using IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);
        await rejected.WhenClosedAsync().WaitAsync(timeout.Token);

        // Assert
        connection.Id.ShouldBe(accepted.Id);
        rejected.IsDisposed.ShouldBeTrue();
        accepted.IsDisposed.ShouldBeFalse();
        amqp.Connections.ShouldHaveSingleItem().ShouldBeSameAs(connection);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should return a later connection while a silent peer's handshake is pending")]
    public async Task AcceptAsync_OnSilentPeer_ShouldReturnLaterConnectionFirst()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection silent = new();
        TestConnection ready = await CreateNegotiatingCarrierAsync(timeout.Token);
        TestConnectionListener listener = new();
        listener.Enqueue(silent);
        listener.Enqueue(ready);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener, new AmqpConnectionListenerOptions
        {
            HandshakeTimeout = _stalledHandshakeTimeout
        });

        // Act
        await using IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);
        byte[] sentToSilentPeer = await silent.ReadBufferedPeerBytesAsync(timeout.Token);

        // Assert
        connection.Id.ShouldBe(ready.Id);
        sentToSilentPeer.ShouldBe(AmqpTestWire.Amqp10Header);
        silent.IsDisposed.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should dispose a carrier whose handshake times out and keep accepting")]
    public async Task AcceptAsync_OnHandshakeTimeout_ShouldDisposeCarrierAndKeepAccepting()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection silent = new();
        TestConnectionListener listener = new();
        listener.Enqueue(silent);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener, new AmqpConnectionListenerOptions
        {
            HandshakeTimeout = _shortHandshakeTimeout
        });
        Task<IAmqpConnection> pendingAccept = amqp.AcceptAsync(timeout.Token).AsTask();

        // Act
        await silent.WhenClosedAsync().WaitAsync(timeout.Token);
        TestConnection ready = await CreateNegotiatingCarrierAsync(timeout.Token);
        listener.Enqueue(ready);
        await using IAmqpConnection connection = await pendingAccept;

        // Assert
        silent.IsDisposed.ShouldBeTrue();
        connection.Id.ShouldBe(ready.Id);
        amqp.Connections.ShouldHaveSingleItem().ShouldBeSameAs(connection);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should not accept another carrier while every handshake slot is held")]
    public async Task AcceptAsync_WithEveryHandshakeSlotHeld_ShouldNotAcceptAnotherCarrier()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection silent = new();
        TestConnection waiting = await CreateNegotiatingCarrierAsync(timeout.Token);
        TestConnectionListener listener = new();
        listener.Enqueue(silent);
        listener.Enqueue(waiting);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener, new AmqpConnectionListenerOptions
        {
            HandshakeTimeout = _stalledHandshakeTimeout,
            MaxConcurrentHandshakes = 1
        });
        using CancellationTokenSource acceptWindow = new(TimeSpan.FromMilliseconds(500));

        // Act
        Task<IAmqpConnection> accept = amqp.AcceptAsync(acceptWindow.Token).AsTask();
        byte[] sentToSilentPeer = await silent.ReadBufferedPeerBytesAsync(timeout.Token);

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(accept);
        sentToSilentPeer.ShouldBe(AmqpTestWire.Amqp10Header);
        waiting.HasBufferedPeerBytes.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DisposeAsync: Should dispose the carrier listener and leave accepted connections open")]
    public async Task DisposeAsync_OnListener_ShouldDisposeCarrierListenerAndLeaveAcceptedConnectionsOpen()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = await CreateNegotiatingCarrierAsync(timeout.Token);
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);
        AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener);
        await using IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);

        // Act
        await amqp.DisposeAsync();

        // Assert
        listener.IsDisposed.ShouldBeTrue();
        carrier.IsDisposed.ShouldBeFalse();
        connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DisposeAsync: Should dispose a carrier still in its handshake and fail the pending accept")]
    public async Task DisposeAsync_DuringHandshake_ShouldDisposeCarrierAndFailPendingAccept()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection silent = new();
        TestConnectionListener listener = new();
        listener.Enqueue(silent);
        AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener, new AmqpConnectionListenerOptions
        {
            HandshakeTimeout = _stalledHandshakeTimeout
        });
        Task<IAmqpConnection> pendingAccept = amqp.AcceptAsync(timeout.Token).AsTask();

        // The listener has sent its protocol header, so the handshake waits for the silent peer's.
        await silent.ReadBufferedPeerBytesAsync(timeout.Token);

        // Act
        await amqp.DisposeAsync();

        // Assert
        silent.IsDisposed.ShouldBeTrue();
        listener.IsDisposed.ShouldBeTrue();
        await Should.ThrowAsync<ObjectDisposedException>(pendingAccept);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should throw after the listener is disposed")]
    public async Task AcceptAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        TestConnectionListener listener = new();
        AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener);
        await amqp.DisposeAsync();

        // Act + Assert
        await Should.ThrowAsync<ObjectDisposedException>(async () => await amqp.AcceptAsync(CancellationToken.None));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DisposeAsync: Should dispose the carrier and untrack the accepted connection")]
    public async Task DisposeAsync_OnAcceptedConnection_ShouldDisposeCarrierAndUntrackConnection()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener, ManualNegotiationOptions);
        IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);

        // Act
        await connection.DisposeAsync();

        // Assert
        carrier.IsDisposed.ShouldBeTrue();
        connection.State.ShouldBe(ConnectionState.Closed);
        amqp.Connections.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Abort: Should abort the carrier connection")]
    public async Task Abort_OnAcceptedConnection_ShouldAbortCarrier()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener, ManualNegotiationOptions);
        await using IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);
        InvalidOperationException reason = new("test abort");

        // Act
        connection.Abort(reason);

        // Assert
        carrier.IsAborted.ShouldBeTrue();
        carrier.AbortReason.ShouldBeSameAs(reason);
        connection.State.ShouldBe(ConnectionState.Aborted);
        connection.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        amqp.Connections.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - BindAsync: Should bind the carrier listener of either carrier kind")]
    public async Task BindAsync_OnEitherCarrierKind_ShouldBindCarrierListener()
    {
        // Arrange
        TestConnectionListener listener = new();
        TestMultiplexedConnectionListener multiplexedListener = new();
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener);
        await using AmqpConnectionListener multiplexedAmqp = AmqpConnectionListener.Create(multiplexedListener);

        // Act
        await amqp.BindAsync(CancellationToken.None);
        await multiplexedAmqp.BindAsync(CancellationToken.None);

        // Assert
        listener.BindCount.ShouldBe(1);
        multiplexedListener.BindCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - BindAsync: Should throw after the listener is disposed")]
    public async Task BindAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        TestConnectionListener listener = new();
        AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener);
        await amqp.DisposeAsync();

        // Act + Assert
        await Should.ThrowAsync<ObjectDisposedException>(async () => await amqp.BindAsync(CancellationToken.None));
        listener.BindCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should report the carrier listener's identity, endpoint and capabilities")]
    public async Task Create_OnSingleStreamCarrier_ShouldReportCarrierListener()
    {
        // Arrange
        TestConnectionListener listener = new();

        // Act
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener);

        // Assert
        amqp.Id.ShouldBe(listener.Id);
        amqp.EndPoint.ShouldBeSameAs(listener.EndPoint);
        amqp.Capabilities.ShouldBe(listener.Capabilities);
        amqp.Connections.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should report a multiplexed carrier as one byte stream")]
    public async Task Create_OnMultiplexedCarrier_ShouldReportOneByteStream()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection stream = await CreateNegotiatingCarrierAsync(timeout.Token);
        TestMultiplexedConnection carrier = new();
        carrier.Enqueue(stream);
        TestMultiplexedConnectionListener listener = new();
        listener.Enqueue(carrier);

        // Act
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener);
        await using IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);

        // Assert
        amqp.Id.ShouldBe(listener.Id);
        amqp.EndPoint.ShouldBeSameAs(listener.EndPoint);
        amqp.Capabilities.ShouldBe(listener.Capabilities with { IsMultiplexed = false });
        connection.Capabilities.ShouldBe(carrier.Capabilities with { IsMultiplexed = false });
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Features: Should expose the features of a single-stream carrier")]
    public async Task Features_OnSingleStreamCarrier_ShouldExposeCarrierFeatures()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnectionFeature feature = new("test.carrier");
        TestConnection carrier = new();
        carrier.Features.Set(feature);
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener, ManualNegotiationOptions);

        // Act
        await using IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);

        // Assert
        connection.Features.Get<TestConnectionFeature>().ShouldBeSameAs(feature);
        connection.Features.Get("test.carrier").ShouldBeSameAs(feature);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Features: Should expose the features of a multiplexed carrier")]
    public async Task Features_OnMultiplexedCarrier_ShouldExposeCarrierFeatures()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnectionFeature feature = new("test.multiplexed");
        TestMultiplexedConnection carrier = new();
        carrier.Features.Set(feature);
        carrier.Enqueue(new TestConnection());
        TestMultiplexedConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener, ManualNegotiationOptions);

        // Act
        await using IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);

        // Assert
        connection.Features.Get<TestConnectionFeature>().ShouldBeSameAs(feature);
        connection.Features.Get("test.multiplexed").ShouldBeSameAs(feature);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectionClosed: Should end the connection when its AMQP stream ends on an open multiplexed carrier")]
    public async Task ConnectionClosed_OnAmqpStreamEnded_ShouldEndConnection()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection stream = await CreateNegotiatingCarrierAsync(timeout.Token);
        TestMultiplexedConnection carrier = new();
        carrier.Enqueue(stream);
        TestMultiplexedConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener);
        await using IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);

        // Act
        stream.Abort();

        // Assert
        connection.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        connection.State.ShouldBe(ConnectionState.Aborted);
        carrier.State.ShouldBe(ConnectionState.Open);
        amqp.Connections.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DisposeAsync: Should dispose the AMQP stream and the multiplexed carrier")]
    public async Task DisposeAsync_OnMultiplexedConnection_ShouldDisposeStreamAndCarrier()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection stream = new();
        TestMultiplexedConnection carrier = new();
        carrier.Enqueue(stream);
        TestMultiplexedConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener, ManualNegotiationOptions);
        IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);

        // Act
        await connection.DisposeAsync();

        // Assert
        stream.IsDisposed.ShouldBeTrue();
        carrier.IsDisposed.ShouldBeTrue();
        connection.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        amqp.Connections.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should dispose a multiplexed carrier whose first stream is unidirectional and keep accepting")]
    public async Task AcceptAsync_OnUnidirectionalStream_ShouldDisposeCarrierAndReturnNextConnection()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection unidirectional = new() { StreamDirection = ConnectionDirection.ReadOnly };
        TestMultiplexedConnection refused = new();
        refused.Enqueue(unidirectional);
        TestConnection stream = await CreateNegotiatingCarrierAsync(timeout.Token);
        TestMultiplexedConnection accepted = new();
        accepted.Enqueue(stream);
        TestMultiplexedConnectionListener listener = new();
        listener.Enqueue(refused);
        listener.Enqueue(accepted);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener);

        // Act
        await using IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);
        await refused.WhenClosedAsync().WaitAsync(timeout.Token);

        // Assert
        connection.Id.ShouldBe(accepted.Id);
        refused.IsDisposed.ShouldBeTrue();
        unidirectional.IsDisposed.ShouldBeTrue();
        unidirectional.HasBufferedPeerBytes.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should dispose a multiplexed carrier whose peer opens no stream within the handshake timeout")]
    public async Task AcceptAsync_OnPeerOpeningNoStream_ShouldDisposeCarrierAfterHandshakeTimeout()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestMultiplexedConnection streamless = new();
        TestMultiplexedConnectionListener listener = new();
        listener.Enqueue(streamless);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener, new AmqpConnectionListenerOptions
        {
            HandshakeTimeout = _shortHandshakeTimeout
        });
        Task<IAmqpConnection> pendingAccept = amqp.AcceptAsync(timeout.Token).AsTask();

        // Act
        await streamless.WhenClosedAsync().WaitAsync(timeout.Token);
        TestConnection stream = await CreateNegotiatingCarrierAsync(timeout.Token);
        TestMultiplexedConnection ready = new();
        ready.Enqueue(stream);
        listener.Enqueue(ready);
        await using IAmqpConnection connection = await pendingAccept;

        // Assert
        streamless.AcceptStreamCount.ShouldBe(1);
        streamless.IsDisposed.ShouldBeTrue();
        connection.Id.ShouldBe(ready.Id);
    }
}
