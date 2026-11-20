using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

public class AmqpConnectionTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(5);

    private static readonly EndPoint _testEndPoint = new IPEndPoint(IPAddress.Loopback, 5672);

    private static AmqpConnectionFactoryOptions ManualNegotiationOptions => new()
    {
        AutoNegotiateProtocolHeader = false
    };

    private static async Task<IAmqpConnection> ConnectAsync(
        TestConnection carrier,
        AmqpConnectionFactoryOptions? options,
        CancellationToken cancellationToken)
    {
        TestConnectionFactory factory = new();
        factory.Enqueue(carrier);

        return await AmqpConnectionFactory.Create(factory, options).ConnectAsync(_testEndPoint, cancellationToken);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReceiveAsync: Should decode an inbound open performative after negotiation")]
    public async Task ReceiveAsync_OnInboundOpenFrame_ShouldDecodeOpenPerformative()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        await carrier.WritePeerAsync(AmqpTestWire.Amqp10Header, timeout.Token);
        await using IAmqpConnection connection = await ConnectAsync(carrier, options: null, timeout.Token);

        // Act
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
        AmqpFrame frame = await AmqpTestWire.ReceiveFrameAsync(connection, timeout.Token);

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
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        await carrier.WritePeerAsync(
            AmqpTestWire.Combine(
                AmqpTestWire.Amqp10Header,
                AmqpFrameCodec.Encode(new AmqpFrame(0, new AmqpOpenPerformative { ContainerId = "remote-container" }))),
            timeout.Token);
        await using IAmqpConnection connection = await ConnectAsync(carrier, options: null, timeout.Token);

        // Act
        AmqpFrame frame = await AmqpTestWire.ReceiveFrameAsync(connection, timeout.Token);

        // Assert
        frame.Performative.ShouldBeOfType<AmqpOpenPerformative>().ContainerId.ShouldBe("remote-container");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReceiveAsync: Should complete without frames when the peer ends the stream between frames")]
    public async Task ReceiveAsync_OnPeerEndingBetweenFrames_ShouldCompleteWithoutFrames()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        await carrier.WritePeerAsync(AmqpTestWire.Amqp10Header, timeout.Token);
        carrier.CompletePeerOutput();
        await using IAmqpConnection connection = await ConnectAsync(carrier, options: null, timeout.Token);
        int frameCount = 0;

        // Act
        await foreach (AmqpFrame frame in connection.ReceiveAsync(timeout.Token))
        {
            frameCount++;
        }

        // Assert
        frameCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should write the encoded begin frame to the carrier")]
    public async Task SendAsync_OnBeginFrame_ShouldWriteEncodedFrameToCarrier()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        await carrier.WritePeerAsync(AmqpTestWire.Amqp10Header, timeout.Token);
        await using IAmqpConnection connection = await ConnectAsync(carrier, options: null, timeout.Token);

        // Act
        await connection.SendAsync(
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
        outbound.AsSpan(0, 8).ToArray().ShouldBe(AmqpTestWire.Amqp10Header);
        AmqpFrame beginFrame = AmqpFrameCodec.Decode(outbound.AsSpan(8));
        AmqpBeginPerformative begin = beginFrame.Performative.ShouldBeOfType<AmqpBeginPerformative>();
        begin.NextOutgoingId.ShouldBe((uint)1);
        begin.IncomingWindow.ShouldBe((uint)32);
        begin.OutgoingWindow.ShouldBe((uint)32);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AsStream: Should round-trip bytes over the carrier connection")]
    public async Task AsStream_OnEstablishedConnection_ShouldRoundTripCarrierBytes()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        await using IAmqpConnection connection = await ConnectAsync(carrier, ManualNegotiationOptions, timeout.Token);
        Stream stream = connection.AsStream();

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
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        await using IAmqpConnection connection = await ConnectAsync(carrier, ManualNegotiationOptions, timeout.Token);
        AmqpFrame frame = new(0, new AmqpClosePerformative());

        // Act
        await Should.ThrowAsync<InvalidOperationException>(async () => await connection.SendAsync(frame, timeout.Token));

        // Assert
        carrier.HasBufferedPeerBytes.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReceiveAsync: Should require negotiation when auto-negotiation is disabled")]
    public async Task ReceiveAsync_WithoutNegotiation_ShouldThrowInvalidOperationException()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        await using IAmqpConnection connection = await ConnectAsync(carrier, ManualNegotiationOptions, timeout.Token);

        // Act + Assert
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (AmqpFrame frame in connection.ReceiveAsync(timeout.Token))
            {
            }
        });
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReceiveAsync: Should surface a protocol error when the carrier ends mid-frame")]
    public async Task ReceiveAsync_OnTruncatedFrame_ShouldThrowAmqpProtocolException()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        byte[] truncatedFrame = { 0, 0, 0, 100, 2, 0, 0, 0 };
        await carrier.WritePeerAsync(AmqpTestWire.Combine(AmqpTestWire.Amqp10Header, truncatedFrame), timeout.Token);
        carrier.CompletePeerOutput();
        await using IAmqpConnection connection = await ConnectAsync(carrier, options: null, timeout.Token);

        // Act + Assert
        await Should.ThrowAsync<AmqpProtocolException>(async () =>
        {
            await foreach (AmqpFrame frame in connection.ReceiveAsync(timeout.Token))
            {
            }
        });
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should reject frames exceeding the configured maximum frame size")]
    public async Task SendAsync_OnFrameExceedingMaxFrameSize_ShouldThrowAmqpProtocolException()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        await carrier.WritePeerAsync(AmqpTestWire.Amqp10Header, timeout.Token);
        await using IAmqpConnection connection = await ConnectAsync(carrier, new AmqpConnectionFactoryOptions { MaxFrameSize = 512 }, timeout.Token);
        await carrier.ReadBufferedPeerBytesAsync(timeout.Token);
        AmqpFrame oversizedFrame = new(0, new AmqpTransferPerformative { Handle = 1 }, new byte[1024]);

        // Act
        await Should.ThrowAsync<AmqpProtocolException>(async () => await connection.SendAsync(oversizedFrame, timeout.Token));

        // Assert
        carrier.HasBufferedPeerBytes.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should reject a null frame")]
    public async Task SendAsync_OnNullFrame_ShouldThrowArgumentNullException()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        await carrier.WritePeerAsync(AmqpTestWire.Amqp10Header, timeout.Token);
        await using IAmqpConnection connection = await ConnectAsync(carrier, options: null, timeout.Token);

        // Act
        ArgumentNullException exception = await Should.ThrowAsync<ArgumentNullException>(async () => await connection.SendAsync(null!, timeout.Token));

        // Assert
        exception.ParamName.ShouldBe("frame");
    }
}
