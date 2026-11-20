using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

public class AmqpProtocolNegotiationTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(5);

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - NegotiateAsync: Should exchange the protocol headers on call when auto-negotiation is disabled")]
    public async Task NegotiateAsync_WithoutAutoNegotiation_ShouldExchangeProtocolHeaders()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener, new AmqpConnectionListenerOptions
        {
            AutoNegotiateProtocolHeader = false
        });
        await using IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);
        bool bytesBeforeNegotiation = carrier.HasBufferedPeerBytes;
        await carrier.WritePeerAsync(AmqpTestWire.Amqp10Header, timeout.Token);

        // Act
        AmqpProtocolHeader negotiated = await connection.NegotiateAsync(timeout.Token);
        byte[] sentToPeer = await carrier.ReadBufferedPeerBytesAsync(timeout.Token);

        // Assert
        bytesBeforeNegotiation.ShouldBeFalse();
        negotiated.ShouldBe(AmqpProtocolHeader.Amqp10);
        connection.RemoteProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        sentToPeer.ShouldBe(AmqpTestWire.Amqp10Header);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - NegotiateAsync: Should return the cached remote header on repeated calls")]
    public async Task NegotiateAsync_OnRepeatedCalls_ShouldReturnCachedRemoteHeaderWithoutMoreBytes()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        await carrier.WritePeerAsync(AmqpTestWire.Amqp10Header, timeout.Token);
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener);
        await using IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);
        await carrier.ReadBufferedPeerBytesAsync(timeout.Token);

        // Act
        AmqpProtocolHeader renegotiated = await connection.NegotiateAsync(timeout.Token);

        // Assert
        renegotiated.ShouldBe(AmqpProtocolHeader.Amqp10);
        carrier.HasBufferedPeerBytes.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SwitchProtocolAsync: Should negotiate the next protocol phase after SASL")]
    public async Task SwitchProtocolAsync_AfterSaslNegotiation_ShouldNegotiateNextPhase()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        await carrier.WritePeerAsync(AmqpTestWire.Sasl10Header, timeout.Token);
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener, new AmqpConnectionListenerOptions
        {
            InitialProtocolHeader = AmqpProtocolHeader.Sasl10
        });
        await using IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);
        AmqpProtocolHeader? saslHeader = connection.RemoteProtocolHeader;
        await carrier.WritePeerAsync(AmqpTestWire.Amqp10Header, timeout.Token);

        // Act
        AmqpProtocolHeader amqpHeader = await connection.SwitchProtocolAsync(AmqpProtocolHeader.Amqp10, timeout.Token);
        byte[] sentToPeer = await carrier.ReadBufferedPeerBytesAsync(timeout.Token);

        // Assert
        saslHeader.ShouldBe(AmqpProtocolHeader.Sasl10);
        amqpHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        connection.LocalProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        connection.RemoteProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        sentToPeer.ShouldBe(AmqpTestWire.Combine(AmqpTestWire.Sasl10Header, AmqpTestWire.Amqp10Header));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SwitchProtocolAsync: Should read a peer header pipelined after the SASL frame the connection received")]
    public async Task SwitchProtocolAsync_OnPeerHeaderPipelinedAfterSaslFrame_ShouldReadBufferedHeader()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        await carrier.WritePeerAsync(
            AmqpTestWire.Combine(
                AmqpTestWire.Sasl10Header,
                AmqpFrameCodec.Encode(new AmqpFrame(0, new AmqpSaslInitPerformative { Mechanism = "ANONYMOUS" }), AmqpFrameType.Sasl),
                AmqpTestWire.Amqp10Header),
            timeout.Token);
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener, new AmqpConnectionListenerOptions
        {
            InitialProtocolHeader = AmqpProtocolHeader.Sasl10
        });
        await using IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);
        AmqpFrame saslInit = await AmqpTestWire.ReceiveFrameAsync(connection, timeout.Token);

        // Act
        AmqpProtocolHeader amqpHeader = await connection.SwitchProtocolAsync(AmqpProtocolHeader.Amqp10, timeout.Token);

        // Assert
        saslInit.Performative.ShouldBeOfType<AmqpSaslInitPerformative>().Mechanism.ShouldBe((AmqpSymbol)"ANONYMOUS");
        amqpHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        connection.RemoteProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should encode SASL frames while the connection is in the SASL phase")]
    public async Task SendAsync_InSaslPhase_ShouldWriteSaslFrame()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        await carrier.WritePeerAsync(AmqpTestWire.Sasl10Header, timeout.Token);
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener, new AmqpConnectionListenerOptions
        {
            InitialProtocolHeader = AmqpProtocolHeader.Sasl10
        });
        await using IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);

        // Act
        await connection.SendAsync(
            new AmqpFrame(0, new AmqpSaslMechanismsPerformative { SaslServerMechanisms = new AmqpSymbol[] { "ANONYMOUS" } }),
            timeout.Token);
        byte[] outbound = await carrier.ReadBufferedPeerBytesAsync(timeout.Token);

        // Assert
        outbound.AsSpan(0, 8).ToArray().ShouldBe(AmqpTestWire.Sasl10Header);
        AmqpFrame frame = AmqpFrameCodec.Decode(outbound.AsSpan(8), AmqpFrameType.Sasl);
        frame.Performative.ShouldBeOfType<AmqpSaslMechanismsPerformative>()
            .SaslServerMechanisms.ShouldHaveSingleItem().ShouldBe((AmqpSymbol)"ANONYMOUS");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should negotiate both ends of a single-stream carrier and carry frames between them")]
    public async Task ConnectAsync_OnInMemoryCarrier_ShouldNegotiateBothEndsAndCarryFrames()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        InMemoryConnectionListener carrierListener = new();
        await using AmqpConnectionListener server = AmqpConnectionListener.Create(carrierListener);
        AmqpConnectionFactory client = AmqpConnectionFactory.Create(carrierListener.CreateFactory());
        Task<IAmqpConnection> accept = server.AcceptAsync(timeout.Token).AsTask();

        // Act
        await using IAmqpConnection clientConnection = await client.ConnectAsync(carrierListener.EndPoint, timeout.Token);
        await using IAmqpConnection serverConnection = await accept;
        await clientConnection.SendAsync(new AmqpFrame(0, new AmqpOpenPerformative { ContainerId = "client-container" }), timeout.Token);
        AmqpFrame received = await AmqpTestWire.ReceiveFrameAsync(serverConnection, timeout.Token);

        // Assert
        clientConnection.RemoteProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        serverConnection.RemoteProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        received.Performative.ShouldBeOfType<AmqpOpenPerformative>().ContainerId.ShouldBe("client-container");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should negotiate both ends of a multiplexed carrier over one stream and carry frames between them")]
    public async Task ConnectAsync_OnInMemoryMultiplexedCarrier_ShouldNegotiateBothEndsAndCarryFrames()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        InMemoryMultiplexedConnectionListener carrierListener = new();
        await using AmqpConnectionListener server = AmqpConnectionListener.Create(carrierListener);
        AmqpConnectionFactory client = AmqpConnectionFactory.Create(carrierListener.CreateFactory());
        Task<IAmqpConnection> accept = server.AcceptAsync(timeout.Token).AsTask();

        // Act
        await using IAmqpConnection clientConnection = await client.ConnectAsync(carrierListener.EndPoint, timeout.Token);
        await using IAmqpConnection serverConnection = await accept;
        await serverConnection.SendAsync(new AmqpFrame(0, new AmqpOpenPerformative { ContainerId = "server-container" }), timeout.Token);
        AmqpFrame received = await AmqpTestWire.ReceiveFrameAsync(clientConnection, timeout.Token);

        // Assert
        clientConnection.RemoteProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        serverConnection.RemoteProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        clientConnection.Capabilities.IsMultiplexed.ShouldBeFalse();
        received.Performative.ShouldBeOfType<AmqpOpenPerformative>().ContainerId.ShouldBe("server-container");
    }
}
