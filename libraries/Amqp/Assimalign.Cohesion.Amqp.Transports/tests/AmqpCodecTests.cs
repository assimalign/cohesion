using System;
using System.Collections.Generic;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Amqp.Transports.Tests;

public class AmqpCodecTests
{
    [Fact(DisplayName = "Cohesion Test [Amqp.Transports] - FrameCodec: Should round-trip a transfer frame with an encoded AMQP message payload")]
    public void FrameCodec_OnTransferFrame_ShouldRoundTripTransferPayload()
    {
        // Arrange
        AmqpMessage message = new()
        {
            Subject = "orders.created",
            ApplicationProperties = new Dictionary<string, object?>
            {
                ["tenant"] = "blue",
                ["attempt"] = (uint) 2
            },
            DataSections = new ReadOnlyMemory<byte>[]
            {
                new ReadOnlyMemory<byte>(new byte[] { 0x10, 0x20, 0x30 })
            }
        };

        byte[] payload = AmqpMessageCodec.Encode(message);
        AmqpFrame frame = new(
            3,
            new AmqpTransferPerformative
            {
                Handle = 12,
                DeliveryId = 4,
                DeliveryTag = new byte[] { 0x01, 0x02, 0x03 },
                MessageFormat = 0
            },
            payload);

        // Act
        byte[] encodedFrame = AmqpFrameCodec.Encode(frame);
        AmqpFrame decodedFrame = AmqpFrameCodec.Decode(encodedFrame);
        AmqpMessage decodedMessage = AmqpMessageCodec.Decode(decodedFrame.Payload.Span);

        // Assert
        decodedFrame.Channel.ShouldBe((ushort) 3);
        AmqpTransferPerformative transfer = decodedFrame.Performative.ShouldBeOfType<AmqpTransferPerformative>();
        transfer.Handle.ShouldBe((uint) 12);
        transfer.DeliveryId.ShouldBe((uint) 4);
        transfer.DeliveryTag.ShouldBe(new byte[] { 0x01, 0x02, 0x03 });
        decodedMessage.Subject.ShouldBe("orders.created");
        decodedMessage.ApplicationProperties!["tenant"].ShouldBe("blue");
        decodedMessage.ApplicationProperties["attempt"].ShouldBe((uint) 2);
        decodedMessage.DataSections![0].ToArray().ShouldBe(new byte[] { 0x10, 0x20, 0x30 });
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Transports] - FrameCodec: Should round-trip SASL mechanism negotiation frames")]
    public void FrameCodec_OnSaslFrame_ShouldRoundTripMechanisms()
    {
        // Arrange
        AmqpFrame frame = new(
            0,
            new AmqpSaslMechanismsPerformative
            {
                SaslServerMechanisms = new[] { (AmqpSymbol) "ANONYMOUS", (AmqpSymbol) "PLAIN" }
            });

        // Act
        byte[] encodedFrame = AmqpFrameCodec.Encode(frame, AmqpFrameType.Sasl);
        AmqpFrame decodedFrame = AmqpFrameCodec.Decode(encodedFrame, AmqpFrameType.Sasl);

        // Assert
        AmqpSaslMechanismsPerformative mechanisms = decodedFrame.Performative.ShouldBeOfType<AmqpSaslMechanismsPerformative>();
        mechanisms.SaslServerMechanisms.Count.ShouldBe(2);
        mechanisms.SaslServerMechanisms[0].ShouldBe((AmqpSymbol) "ANONYMOUS");
        mechanisms.SaslServerMechanisms[1].ShouldBe((AmqpSymbol) "PLAIN");
    }
}
