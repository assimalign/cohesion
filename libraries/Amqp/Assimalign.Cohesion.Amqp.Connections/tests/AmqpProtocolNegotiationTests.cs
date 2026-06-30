using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

public class AmqpProtocolNegotiationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static readonly EndPoint TestEndPoint = new IPEndPoint(IPAddress.Loopback, 5672);

    private static readonly byte[] Amqp10HeaderBytes = { (byte)'A', (byte)'M', (byte)'Q', (byte)'P', 0, 1, 0, 0 };

    private static readonly byte[] Sasl10HeaderBytes = { (byte)'A', (byte)'M', (byte)'Q', (byte)'P', 3, 1, 0, 0 };

    private static AmqpServerTransport CreateServerTransport(TestConnection carrier, AmqpTransportOptions? options = null)
    {
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);

        return new AmqpServerTransport(listener, options);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OpenAsync: Should exchange protocol headers when auto-negotiation is enabled")]
    public async Task OpenAsync_WithAutoNegotiate_ShouldExchangeProtocolHeaders()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        TestConnection carrier = new();
        await carrier.WritePeerAsync(Amqp10HeaderBytes, timeout.Token);
        await using AmqpServerTransport transport = CreateServerTransport(carrier);
        AmqpConnection connection = await transport.AcceptAsync(timeout.Token);

        // Act
        AmqpConnectionContext context = await connection.OpenAsync(timeout.Token);
        byte[] sentToPeer = await carrier.ReadBufferedPeerBytesAsync(timeout.Token);

        // Assert
        context.LocalProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        context.RemoteProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        sentToPeer.ShouldBe(Amqp10HeaderBytes);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OpenAsync: Should not exchange bytes until NegotiateAsync when auto-negotiation is disabled")]
    public async Task OpenAsync_WithoutAutoNegotiate_ShouldNotExchangeBytesUntilNegotiateAsync()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        TestConnection carrier = new();
        AmqpTransportOptions options = new() { AutoNegotiateProtocolHeader = false };
        await using AmqpServerTransport transport = CreateServerTransport(carrier, options);
        AmqpConnection connection = await transport.AcceptAsync(timeout.Token);

        // Act
        AmqpConnectionContext context = await connection.OpenAsync(timeout.Token);
        bool bytesBeforeNegotiation = carrier.HasBufferedPeerBytes;

        await carrier.WritePeerAsync(Amqp10HeaderBytes, timeout.Token);
        AmqpProtocolHeader negotiated = await context.NegotiateAsync(timeout.Token);
        byte[] sentToPeer = await carrier.ReadBufferedPeerBytesAsync(timeout.Token);

        // Assert
        bytesBeforeNegotiation.ShouldBeFalse();
        context.RemoteProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        negotiated.ShouldBe(AmqpProtocolHeader.Amqp10);
        sentToPeer.ShouldBe(Amqp10HeaderBytes);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - NegotiateAsync: Should return the cached remote header on repeated calls")]
    public async Task NegotiateAsync_OnRepeatedCalls_ShouldReturnCachedRemoteHeaderWithoutMoreBytes()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        TestConnection carrier = new();
        await carrier.WritePeerAsync(Amqp10HeaderBytes, timeout.Token);
        await using AmqpServerTransport transport = CreateServerTransport(carrier);
        AmqpConnection connection = await transport.AcceptAsync(timeout.Token);
        AmqpConnectionContext context = await connection.OpenAsync(timeout.Token);
        await carrier.ReadBufferedPeerBytesAsync(timeout.Token);

        // Act
        AmqpProtocolHeader renegotiated = await context.NegotiateAsync(timeout.Token);

        // Assert
        renegotiated.ShouldBe(AmqpProtocolHeader.Amqp10);
        carrier.HasBufferedPeerBytes.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SwitchProtocolAsync: Should negotiate the next protocol phase after SASL")]
    public async Task SwitchProtocolAsync_AfterSaslNegotiation_ShouldNegotiateNextPhase()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        TestConnection carrier = new();
        await carrier.WritePeerAsync(Sasl10HeaderBytes, timeout.Token);
        AmqpTransportOptions options = new() { InitialProtocolHeader = AmqpProtocolHeader.Sasl10 };
        await using AmqpServerTransport transport = CreateServerTransport(carrier, options);
        AmqpConnection connection = await transport.AcceptAsync(timeout.Token);

        // Act
        AmqpConnectionContext context = await connection.OpenAsync(timeout.Token);
        AmqpProtocolHeader saslHeader = context.RemoteProtocolHeader!.Value;

        await carrier.WritePeerAsync(Amqp10HeaderBytes, timeout.Token);
        AmqpProtocolHeader amqpHeader = await context.SwitchProtocolAsync(AmqpProtocolHeader.Amqp10, timeout.Token);
        byte[] sentToPeer = await carrier.ReadBufferedPeerBytesAsync(timeout.Token);

        // Assert
        saslHeader.ShouldBe(AmqpProtocolHeader.Sasl10);
        amqpHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        context.LocalProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        context.RemoteProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        sentToPeer.ShouldBe(new byte[] { (byte)'A', (byte)'M', (byte)'Q', (byte)'P', 3, 1, 0, 0, (byte)'A', (byte)'M', (byte)'Q', (byte)'P', 0, 1, 0, 0 });
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OpenAsync: Should negotiate both sides over cross-wired carrier peers")]
    public async Task OpenAsync_OnCrossWiredPeers_ShouldNegotiateBothSides()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        (TestConnection clientCarrier, TestConnection serverCarrier) = TestConnection.CreatePair();

        TestConnectionListener listener = new();
        listener.Enqueue(serverCarrier);
        TestConnectionFactory factory = new();
        factory.Enqueue(clientCarrier);

        await using AmqpServerTransport server = new(listener);
        await using AmqpClientTransport client = new(factory, TestEndPoint);

        AmqpConnection serverConnection = await server.AcceptAsync(timeout.Token);
        AmqpConnection clientConnection = await client.ConnectAsync(timeout.Token);

        // Act
        Task<AmqpConnectionContext> serverOpen = serverConnection.OpenAsync(timeout.Token).AsTask();
        Task<AmqpConnectionContext> clientOpen = clientConnection.OpenAsync(timeout.Token).AsTask();
        await Task.WhenAll(serverOpen, clientOpen);

        // Assert
        (await serverOpen).RemoteProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        (await clientOpen).RemoteProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OpenAsync: Should surface a protocol error when the remote header is truncated")]
    public async Task OpenAsync_OnTruncatedRemoteHeader_ShouldThrowAmqpProtocolException()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        TestConnection carrier = new();
        await carrier.WritePeerAsync(new byte[] { (byte)'A', (byte)'M', (byte)'Q' }, timeout.Token);
        carrier.CompletePeerOutput();
        await using AmqpServerTransport transport = CreateServerTransport(carrier);
        AmqpConnection connection = await transport.AcceptAsync(timeout.Token);

        // Act + Assert
        await Should.ThrowAsync<AmqpProtocolException>(async () => await connection.OpenAsync(timeout.Token));
    }
}
