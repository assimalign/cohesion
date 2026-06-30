using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

public class AmqpConnectionContextTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static readonly byte[] Amqp10HeaderBytes = { (byte)'A', (byte)'M', (byte)'Q', (byte)'P', 0, 1, 0, 0 };

    private static async Task<(AmqpServerTransport Transport, TestConnection Carrier, AmqpConnection Connection)> AcceptAsync(
        AmqpTransportOptions? options,
        CancellationToken cancellationToken)
    {
        TestConnection carrier = new();
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);

        AmqpServerTransport transport = new(listener, options);
        AmqpConnection connection = await transport.AcceptAsync(cancellationToken);

        return (transport, carrier, connection);
    }

    private static async Task<AmqpFrame> ReadSingleFrameAsync(AmqpConnectionContext context, CancellationToken cancellationToken)
    {
        await foreach (AmqpFrame frame in context.ReceiveAsync(cancellationToken))
        {
            return frame;
        }

        throw new InvalidOperationException("Expected a frame but the AMQP receive loop completed.");
    }

    private static byte[] Combine(params byte[][] segments)
    {
        int length = 0;

        foreach (byte[] segment in segments)
        {
            length += segment.Length;
        }

        byte[] buffer = new byte[length];
        int offset = 0;

        foreach (byte[] segment in segments)
        {
            Buffer.BlockCopy(segment, 0, buffer, offset, segment.Length);
            offset += segment.Length;
        }

        return buffer;
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReceiveAsync: Should decode an inbound open performative after negotiation")]
    public async Task ReceiveAsync_OnInboundOpenFrame_ShouldDecodeOpenPerformative()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        (AmqpServerTransport transport, TestConnection carrier, AmqpConnection connection) =
            await AcceptAsync(options: null, timeout.Token);
        await using AmqpServerTransport _ = transport;

        await carrier.WritePeerAsync(Amqp10HeaderBytes, timeout.Token);

        // Act
        AmqpConnectionContext context = await connection.OpenAsync(timeout.Token);

        await carrier.WritePeerAsync(
            AmqpFrameCodec.Encode(new AmqpFrame(
                0,
                new AmqpOpenPerformative
                {
                    ContainerId = "remote-container",
                    MaxFrameSize = 65_536,
                    ChannelMax = 32
                })),
            timeout.Token);
        AmqpFrame frame = await ReadSingleFrameAsync(context, timeout.Token);

        // Assert
        frame.Channel.ShouldBe((ushort)0);
        AmqpOpenPerformative open = frame.Performative.ShouldBeOfType<AmqpOpenPerformative>();
        open.ContainerId.ShouldBe("remote-container");
        open.MaxFrameSize.ShouldBe((uint)65_536);
        open.ChannelMax.ShouldBe((ushort)32);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReceiveAsync: Should decode a frame that arrives coalesced with the protocol header")]
    public async Task ReceiveAsync_OnHeaderCoalescedWithFrame_ShouldDecodeOpenPerformative()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        (AmqpServerTransport transport, TestConnection carrier, AmqpConnection connection) =
            await AcceptAsync(options: null, timeout.Token);
        await using AmqpServerTransport _ = transport;

        byte[] inbound = Combine(
            Amqp10HeaderBytes,
            AmqpFrameCodec.Encode(new AmqpFrame(
                0,
                new AmqpOpenPerformative { ContainerId = "remote-container" })));
        await carrier.WritePeerAsync(inbound, timeout.Token);

        // Act
        AmqpConnectionContext context = await connection.OpenAsync(timeout.Token);
        AmqpFrame frame = await ReadSingleFrameAsync(context, timeout.Token);

        // Assert
        frame.Performative.ShouldBeOfType<AmqpOpenPerformative>().ContainerId.ShouldBe("remote-container");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should write the encoded begin frame to the carrier")]
    public async Task SendAsync_OnBeginFrame_ShouldWriteEncodedFrameToCarrier()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        (AmqpServerTransport transport, TestConnection carrier, AmqpConnection connection) =
            await AcceptAsync(options: null, timeout.Token);
        await using AmqpServerTransport _ = transport;

        await carrier.WritePeerAsync(Amqp10HeaderBytes, timeout.Token);
        AmqpConnectionContext context = await connection.OpenAsync(timeout.Token);

        // Act
        await context.SendAsync(
            new AmqpFrame(
                0,
                new AmqpBeginPerformative
                {
                    NextOutgoingId = 1,
                    IncomingWindow = 32,
                    OutgoingWindow = 32
                }),
            timeout.Token);
        byte[] outbound = await carrier.ReadBufferedPeerBytesAsync(timeout.Token);

        // Assert
        outbound.AsSpan(0, 8).ToArray().ShouldBe(Amqp10HeaderBytes);
        AmqpFrame beginFrame = AmqpFrameCodec.Decode(outbound.AsSpan(8));
        AmqpBeginPerformative begin = beginFrame.Performative.ShouldBeOfType<AmqpBeginPerformative>();
        begin.NextOutgoingId.ShouldBe((uint)1);
        begin.IncomingWindow.ShouldBe((uint)32);
        begin.OutgoingWindow.ShouldBe((uint)32);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AsStream: Should round-trip bytes over the carrier connection")]
    public async Task AsStream_OnOpenedContext_ShouldRoundTripCarrierBytes()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        AmqpTransportOptions options = new() { AutoNegotiateProtocolHeader = false };
        (AmqpServerTransport transport, TestConnection carrier, AmqpConnection connection) =
            await AcceptAsync(options, timeout.Token);
        await using AmqpServerTransport _ = transport;

        AmqpConnectionContext context = await connection.OpenAsync(timeout.Token);
        Stream stream = context.AsStream();

        // Act
        await stream.WriteAsync(new byte[] { 0x01, 0x02 }, timeout.Token);
        byte[] observedByPeer = await carrier.ReadBufferedPeerBytesAsync(timeout.Token);

        await carrier.WritePeerAsync(new byte[] { 0x03, 0x04, 0x05 }, timeout.Token);
        byte[] readBuffer = new byte[3];
        await stream.ReadExactlyAsync(readBuffer, timeout.Token);

        // Assert
        observedByPeer.ShouldBe(new byte[] { 0x01, 0x02 });
        readBuffer.ShouldBe(new byte[] { 0x03, 0x04, 0x05 });
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should require negotiation when auto-negotiation is disabled")]
    public async Task SendAsync_WithoutNegotiation_ShouldThrowInvalidOperationException()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        AmqpTransportOptions options = new() { AutoNegotiateProtocolHeader = false };
        (AmqpServerTransport transport, TestConnection carrier, AmqpConnection connection) =
            await AcceptAsync(options, timeout.Token);
        await using AmqpServerTransport _ = transport;

        AmqpConnectionContext context = await connection.OpenAsync(timeout.Token);
        AmqpFrame frame = new(0, new AmqpClosePerformative());

        // Act + Assert
        await Should.ThrowAsync<InvalidOperationException>(async () => await context.SendAsync(frame, timeout.Token));
        carrier.HasBufferedPeerBytes.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReceiveAsync: Should require negotiation when auto-negotiation is disabled")]
    public async Task ReceiveAsync_WithoutNegotiation_ShouldThrowInvalidOperationException()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        AmqpTransportOptions options = new() { AutoNegotiateProtocolHeader = false };
        (AmqpServerTransport transport, TestConnection carrier, AmqpConnection connection) =
            await AcceptAsync(options, timeout.Token);
        await using AmqpServerTransport _ = transport;

        AmqpConnectionContext context = await connection.OpenAsync(timeout.Token);

        // Act + Assert
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (AmqpFrame frame in context.ReceiveAsync(timeout.Token))
            {
            }
        });
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReceiveAsync: Should surface a protocol error when the carrier ends mid-frame")]
    public async Task ReceiveAsync_OnTruncatedFrame_ShouldThrowAmqpProtocolException()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        (AmqpServerTransport transport, TestConnection carrier, AmqpConnection connection) =
            await AcceptAsync(options: null, timeout.Token);
        await using AmqpServerTransport _ = transport;

        byte[] truncatedFrame = { 0, 0, 0, 100, 2, 0, 0, 0 };
        await carrier.WritePeerAsync(Combine(Amqp10HeaderBytes, truncatedFrame), timeout.Token);
        carrier.CompletePeerOutput();

        AmqpConnectionContext context = await connection.OpenAsync(timeout.Token);

        // Act + Assert
        await Should.ThrowAsync<AmqpProtocolException>(async () =>
        {
            await foreach (AmqpFrame frame in context.ReceiveAsync(timeout.Token))
            {
            }
        });
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should reject frames exceeding the configured maximum frame size")]
    public async Task SendAsync_OnFrameExceedingMaxFrameSize_ShouldThrowAmqpProtocolException()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TestTimeout);
        AmqpTransportOptions options = new() { MaxFrameSize = 512 };
        (AmqpServerTransport transport, TestConnection carrier, AmqpConnection connection) =
            await AcceptAsync(options, timeout.Token);
        await using AmqpServerTransport _ = transport;

        await carrier.WritePeerAsync(Amqp10HeaderBytes, timeout.Token);
        AmqpConnectionContext context = await connection.OpenAsync(timeout.Token);

        AmqpFrame oversizedFrame = new(
            0,
            new AmqpTransferPerformative { Handle = 1 },
            new byte[1024]);

        // Act + Assert
        await Should.ThrowAsync<AmqpProtocolException>(async () => await context.SendAsync(oversizedFrame, timeout.Token));
    }
}
