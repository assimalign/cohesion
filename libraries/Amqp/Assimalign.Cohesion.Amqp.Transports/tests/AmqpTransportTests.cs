using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp.Transports.Tests.TestObjects;
using Assimalign.Cohesion.Transports;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Amqp.Transports.Tests;

public class AmqpTransportTests
{
    [Fact(DisplayName = "Cohesion Test [Amqp.Transports] - Server: Should negotiate the protocol header and receive an open performative")]
    public async Task Server_OnSingleStreamCarrier_ShouldNegotiateHeaderAndReceiveOpenPerformative()
    {
        // Arrange
        byte[] inboundPayload = Combine(
            AmqpProtocolCodec.Encode(AmqpProtocolHeader.Amqp10),
            AmqpFrameCodec.Encode(
                new AmqpFrame(
                    0,
                    new AmqpOpenPerformative
                    {
                        ContainerId = "remote-container",
                        MaxFrameSize = 65_536,
                        ChannelMax = 32
                    })));

        TestTransportConnectionContext carrierContext = new(inboundPayload);
        TestSingleStreamTransportConnection carrierConnection = new(carrierContext, TransportProtocol.Tcp);
        TestTransport carrierTransport = new(TransportKind.Server, TransportProtocol.Tcp, new[] { carrierConnection });

        await using AmqpServerTransport amqpTransport = new(carrierTransport);

        // Act
        IAmqpConnection amqpConnection = await amqpTransport.AcceptOrListenAsync();
        IAmqpConnectionContext amqpContext = await amqpConnection.OpenAsync();
        AmqpFrame frame = await ReadSingleFrameAsync(amqpContext);
        byte[] outboundPayload = await carrierContext.ReadOutputAsync();

        // Assert
        amqpContext.RemoteProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        frame.Channel.ShouldBe((ushort) 0);
        AmqpOpenPerformative open = frame.Performative.ShouldBeOfType<AmqpOpenPerformative>();
        open.ContainerId.ShouldBe("remote-container");
        open.MaxFrameSize.ShouldBe((uint) 65_536);
        open.ChannelMax.ShouldBe((ushort) 32);
        outboundPayload.AsSpan(0, 8).ToArray().ShouldBe(AmqpProtocolCodec.Encode(AmqpProtocolHeader.Amqp10));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Transports] - Client: Should open an outbound stream when the carrier is multiplexed")]
    public async Task Client_OnMultiplexCarrier_ShouldOpenOutboundStreamAndSendBeginPerformative()
    {
        // Arrange
        TestTransportConnectionContext inboundContext = new(Array.Empty<byte>());
        TestTransportConnectionContext outboundContext = new(AmqpProtocolCodec.Encode(AmqpProtocolHeader.Amqp10));
        TestMultiplexTransportConnection carrierConnection = new(inboundContext, outboundContext, TransportProtocol.Quic);
        TestTransport carrierTransport = new(TransportKind.Client, TransportProtocol.Quic, new[] { carrierConnection });

        await using AmqpClientTransport amqpTransport = new(carrierTransport);

        // Act
        IAmqpConnection amqpConnection = await amqpTransport.ConnectAsync();
        IAmqpConnectionContext amqpContext = await amqpConnection.OpenAsync();
        await amqpContext.SendAsync(new AmqpFrame(
            0,
            new AmqpBeginPerformative
            {
                NextOutgoingId = 1,
                IncomingWindow = 32,
                OutgoingWindow = 32
            }));

        byte[] outboundPayload = await outboundContext.ReadOutputAsync();
        AmqpFrame beginFrame = AmqpFrameCodec.Decode(outboundPayload.AsSpan(8), AmqpFrameType.Amqp);

        // Assert
        carrierConnection.OutboundOpenCount.ShouldBe(1);
        carrierConnection.InboundOpenCount.ShouldBe(0);
        beginFrame.Performative.ShouldBeOfType<AmqpBeginPerformative>().NextOutgoingId.ShouldBe((uint) 1);
    }

    private static async Task<AmqpFrame> ReadSingleFrameAsync(IAmqpConnectionContext context)
    {
        await foreach (AmqpFrame frame in context.ReceiveAsync())
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
}
