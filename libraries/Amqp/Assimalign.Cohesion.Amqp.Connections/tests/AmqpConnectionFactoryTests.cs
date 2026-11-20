using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

public class AmqpConnectionFactoryTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(5);

    // Long enough that a stalled handshake is still pending when the test ends.
    private static readonly TimeSpan _stalledHandshakeTimeout = TimeSpan.FromMinutes(5);

    // Short enough that a stalled handshake times out well within the test timeout.
    private static readonly TimeSpan _shortHandshakeTimeout = TimeSpan.FromMilliseconds(200);

    private static readonly EndPoint _testEndPoint = new IPEndPoint(IPAddress.Loopback, 5672);

    private static AmqpConnectionFactoryOptions ManualNegotiationOptions => new()
    {
        AutoNegotiateProtocolHeader = false
    };

    private static async Task<TestConnection> CreateNegotiatingCarrierAsync(CancellationToken cancellationToken)
    {
        TestConnection carrier = new();
        await carrier.WritePeerAsync(AmqpTestWire.Amqp10Header, cancellationToken);

        return carrier;
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should return a negotiated connection over a single-stream carrier")]
    public async Task ConnectAsync_OnSingleStreamCarrier_ShouldReturnNegotiatedConnection()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = await CreateNegotiatingCarrierAsync(timeout.Token);
        TestConnectionFactory factory = new();
        factory.Enqueue(carrier);
        AmqpConnectionFactory amqp = AmqpConnectionFactory.Create(factory);

        // Act
        await using IAmqpConnection connection = await amqp.ConnectAsync(_testEndPoint, timeout.Token);
        byte[] sentToPeer = await carrier.ReadBufferedPeerBytesAsync(timeout.Token);

        // Assert
        connection.Id.ShouldBe(carrier.Id);
        connection.State.ShouldBe(ConnectionState.Open);
        connection.LocalProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        connection.RemoteProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        factory.LastEndPoint.ShouldBeSameAs(_testEndPoint);
        sentToPeer.ShouldBe(AmqpTestWire.Amqp10Header);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should return the connection before any bytes when auto-negotiation is disabled")]
    public async Task ConnectAsync_WithoutAutoNegotiation_ShouldReturnConnectionBeforeAnyBytes()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        TestConnectionFactory factory = new();
        factory.Enqueue(carrier);
        AmqpConnectionFactory amqp = AmqpConnectionFactory.Create(factory, ManualNegotiationOptions);

        // Act
        await using IAmqpConnection connection = await amqp.ConnectAsync(_testEndPoint, timeout.Token);

        // Assert
        connection.Id.ShouldBe(carrier.Id);
        connection.RemoteProtocolHeader.ShouldBeNull();
        carrier.HasBufferedPeerBytes.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should open a bidirectional AMQP stream on a multiplexed carrier")]
    public async Task ConnectAsync_OnMultiplexedCarrier_ShouldOpenBidirectionalAmqpStream()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection stream = await CreateNegotiatingCarrierAsync(timeout.Token);
        TestMultiplexedConnection carrier = new();
        carrier.Enqueue(stream);
        TestMultiplexedConnectionFactory factory = new();
        factory.Enqueue(carrier);
        AmqpConnectionFactory amqp = AmqpConnectionFactory.Create(factory);

        // Act
        await using IAmqpConnection connection = await amqp.ConnectAsync(_testEndPoint, timeout.Token);
        byte[] sentOnStream = await stream.ReadBufferedPeerBytesAsync(timeout.Token);

        // Assert
        connection.Id.ShouldBe(carrier.Id);
        connection.RemoteProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        carrier.OpenStreamCount.ShouldBe(1);
        carrier.AcceptStreamCount.ShouldBe(0);
        carrier.LastOpenedDirection.ShouldBe(ConnectionDirection.Bidirectional);
        factory.LastEndPoint.ShouldBeSameAs(_testEndPoint);
        sentOnStream.ShouldBe(AmqpTestWire.Amqp10Header);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should throw and dispose the carrier when the peer's protocol header is truncated")]
    public async Task ConnectAsync_OnTruncatedPeerHeader_ShouldThrowAndDisposeCarrier()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        await carrier.WritePeerAsync(new byte[] { (byte)'A', (byte)'M', (byte)'Q' }, timeout.Token);
        carrier.CompletePeerOutput();
        TestConnectionFactory factory = new();
        factory.Enqueue(carrier);
        AmqpConnectionFactory amqp = AmqpConnectionFactory.Create(factory);

        // Act
        await Should.ThrowAsync<AmqpProtocolException>(async () => await amqp.ConnectAsync(_testEndPoint, timeout.Token));

        // Assert
        carrier.IsDisposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should throw a timeout and dispose the carrier when the peer stays silent")]
    public async Task ConnectAsync_OnSilentPeer_ShouldThrowTimeoutExceptionAndDisposeCarrier()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection silent = new();
        TestConnectionFactory factory = new();
        factory.Enqueue(silent);
        AmqpConnectionFactory amqp = AmqpConnectionFactory.Create(factory, new AmqpConnectionFactoryOptions
        {
            HandshakeTimeout = _shortHandshakeTimeout
        });

        // Act
        TimeoutException exception = await Should.ThrowAsync<TimeoutException>(async () => await amqp.ConnectAsync(_testEndPoint, timeout.Token));

        // Assert
        exception.InnerException.ShouldBeAssignableTo<OperationCanceledException>();
        silent.IsDisposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should throw a cancellation, not a timeout, and dispose the carrier when the caller cancels")]
    public async Task ConnectAsync_OnCallerCancellation_ShouldThrowOperationCanceledExceptionAndDisposeCarrier()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using CancellationTokenSource caller = new();
        TestConnection silent = new();
        TestConnectionFactory factory = new();
        factory.Enqueue(silent);
        AmqpConnectionFactory amqp = AmqpConnectionFactory.Create(factory, new AmqpConnectionFactoryOptions
        {
            HandshakeTimeout = _stalledHandshakeTimeout
        });
        Task<IAmqpConnection> connect = amqp.ConnectAsync(_testEndPoint, caller.Token).AsTask();

        // The factory has sent its protocol header, so the handshake waits for the silent peer's.
        await silent.ReadBufferedPeerBytesAsync(timeout.Token);

        // Act
        caller.Cancel();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(connect);
        silent.IsDisposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should reject a null endpoint without connecting a carrier")]
    public async Task ConnectAsync_OnNullEndPoint_ShouldThrowArgumentNullException()
    {
        // Arrange
        TestConnectionFactory factory = new();
        AmqpConnectionFactory amqp = AmqpConnectionFactory.Create(factory);

        // Act
        ArgumentNullException exception = await Should.ThrowAsync<ArgumentNullException>(async () => await amqp.ConnectAsync(null!, CancellationToken.None));

        // Assert
        exception.ParamName.ShouldBe("endPoint");
        factory.LastEndPoint.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should connect a new carrier on every call")]
    public async Task ConnectAsync_OnRepeatedCalls_ShouldConnectNewCarrierEachTime()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection first = new();
        TestConnection second = new();
        TestConnectionFactory factory = new();
        factory.Enqueue(first);
        factory.Enqueue(second);
        AmqpConnectionFactory amqp = AmqpConnectionFactory.Create(factory, ManualNegotiationOptions);

        // Act
        await using IAmqpConnection firstConnection = await amqp.ConnectAsync(_testEndPoint, timeout.Token);
        await using IAmqpConnection secondConnection = await amqp.ConnectAsync(_testEndPoint, timeout.Token);

        // Assert
        firstConnection.Id.ShouldBe(first.Id);
        secondConnection.Id.ShouldBe(second.Id);
        secondConnection.ShouldNotBeSameAs(firstConnection);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DisposeAsync: Should dispose the AMQP stream and the multiplexed carrier of a connected connection")]
    public async Task DisposeAsync_OnMultiplexedConnection_ShouldDisposeStreamAndCarrier()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection stream = new();
        TestMultiplexedConnection carrier = new();
        carrier.Enqueue(stream);
        TestMultiplexedConnectionFactory factory = new();
        factory.Enqueue(carrier);
        AmqpConnectionFactory amqp = AmqpConnectionFactory.Create(factory, ManualNegotiationOptions);
        IAmqpConnection connection = await amqp.ConnectAsync(_testEndPoint, timeout.Token);

        // Act
        await connection.DisposeAsync();

        // Assert
        stream.IsDisposed.ShouldBeTrue();
        carrier.IsDisposed.ShouldBeTrue();
        connection.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should dispose the AMQP stream and the multiplexed carrier when the handshake fails")]
    public async Task ConnectAsync_OnFailedMultiplexedHandshake_ShouldDisposeStreamAndCarrier()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection stream = new();
        await stream.WritePeerAsync(AmqpTestWire.NotAProtocolHeader, timeout.Token);
        TestMultiplexedConnection carrier = new();
        carrier.Enqueue(stream);
        TestMultiplexedConnectionFactory factory = new();
        factory.Enqueue(carrier);
        AmqpConnectionFactory amqp = AmqpConnectionFactory.Create(factory);

        // Act
        await Should.ThrowAsync<AmqpProtocolException>(async () => await amqp.ConnectAsync(_testEndPoint, timeout.Token));

        // Assert
        stream.IsDisposed.ShouldBeTrue();
        carrier.IsDisposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Capabilities: Should report either carrier kind as one byte stream")]
    public void Capabilities_OnEitherCarrierKind_ShouldReportOneByteStream()
    {
        // Arrange
        TestConnectionFactory factory = new();
        TestMultiplexedConnectionFactory multiplexedFactory = new();

        // Act
        AmqpConnectionFactory amqp = AmqpConnectionFactory.Create(factory);
        AmqpConnectionFactory multiplexedAmqp = AmqpConnectionFactory.Create(multiplexedFactory);

        // Assert
        amqp.Capabilities.ShouldBe(factory.Capabilities);
        multiplexedAmqp.Capabilities.ShouldBe(multiplexedFactory.Capabilities with { IsMultiplexed = false });
    }
}
