using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The <c>transfer</c> performative: carries a frame of a delivery's payload on a link (AMQP 1.0 Part 2,
/// section 2.7.5). The payload follows the performative in the frame body.
/// </summary>
internal sealed class AmqpTransferPerformative : AmqpPerformative
{
    public override ulong Descriptor => AmqpDescriptor.Transfer;

    public uint Handle { get; set; }

    public uint? DeliveryId { get; set; }

    public AmqpDeliveryTag? DeliveryTag { get; set; }

    public uint? MessageFormat { get; set; }

    public bool? Settled { get; set; }

    public bool More { get; set; }

    public AmqpReceiverSettleMode? ReceiverSettleMode { get; set; }

    public AmqpDeliveryState? State { get; set; }

    public bool Resume { get; set; }

    public bool Aborted { get; set; }

    public bool Batchable { get; set; }

    public override void Encode(AmqpBufferWriter writer)
    {
        AmqpListWriter list = new(writer, Descriptor);

        list.WriteUInt(Handle);
        list.WriteUInt(DeliveryId);

        if (DeliveryTag is AmqpDeliveryTag tag)
        {
            list.WriteBinary(tag.Value.Span);
        }
        else
        {
            list.WriteNull();
        }

        list.WriteUInt(MessageFormat);
        list.WriteBoolean(Settled);
        list.WriteBoolean(More, false);
        list.WriteUByte((byte?)ReceiverSettleMode);
        AmqpTypeCodec.WriteDeliveryState(writer, State);
        list.Field(State is null);
        list.WriteBoolean(Resume, false);
        list.WriteBoolean(Aborted, false);
        list.WriteBoolean(Batchable, false);
        list.Complete();
    }

    public static AmqpTransferPerformative Decode(ref AmqpReader fields, int count)
    {
        uint? handle = count > 0 ? fields.ReadNullableUInt() : null;
        AmqpTransferPerformative transfer = new()
        {
            Handle = handle ?? throw MandatoryFieldMissing("transfer", "handle"),
            DeliveryId = count > 1 ? fields.ReadNullableUInt() : null
        };

        if (count > 2 && fields.ReadBinary(out System.ReadOnlySpan<byte> tag))
        {
            if (!AmqpDeliveryTag.TryCreate(tag, out AmqpDeliveryTag deliveryTag))
            {
                throw AmqpException.ProtocolViolation(AmqpErrorCondition.InvalidField, ErrorMessages.DeliveryTagInvalid);
            }

            transfer.DeliveryTag = deliveryTag;
        }

        transfer.MessageFormat = count > 3 ? fields.ReadNullableUInt() : null;
        transfer.Settled = count > 4 ? fields.ReadNullableBoolean() : null;
        transfer.More = count > 5 && fields.ReadBoolean(false);

        byte? receiverSettleMode = count > 6 ? fields.ReadNullableUByte() : null;

        if (receiverSettleMode > (byte)AmqpReceiverSettleMode.Second)
        {
            throw AmqpException.ProtocolViolation(AmqpErrorCondition.InvalidField, ErrorMessages.ReceiverSettleModeInvalid);
        }

        transfer.ReceiverSettleMode = (AmqpReceiverSettleMode?)receiverSettleMode;
        transfer.State = count > 7 ? AmqpTypeCodec.ReadDeliveryState(ref fields) : null;
        transfer.Resume = count > 8 && fields.ReadBoolean(false);
        transfer.Aborted = count > 9 && fields.ReadBoolean(false);
        transfer.Batchable = count > 10 && fields.ReadBoolean(false);

        return transfer;
    }
}
