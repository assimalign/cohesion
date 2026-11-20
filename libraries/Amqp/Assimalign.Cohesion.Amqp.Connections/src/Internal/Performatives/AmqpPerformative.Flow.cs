using System.Collections.Generic;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The <c>flow</c> performative: updates a session's transfer windows and, with a handle, a link's credit
/// (AMQP 1.0 Part 2, section 2.7.4).
/// </summary>
internal sealed class AmqpFlowPerformative : AmqpPerformative
{
    public override ulong Descriptor => AmqpDescriptor.Flow;

    public uint? NextIncomingId { get; set; }

    public uint IncomingWindow { get; set; }

    public uint NextOutgoingId { get; set; }

    public uint OutgoingWindow { get; set; }

    public uint? Handle { get; set; }

    public uint? DeliveryCount { get; set; }

    public uint? LinkCredit { get; set; }

    public uint? Available { get; set; }

    public bool Drain { get; set; }

    public bool Echo { get; set; }

    public IReadOnlyDictionary<AmqpSymbol, object?>? Properties { get; set; }

    public override void Encode(AmqpBufferWriter writer)
    {
        AmqpListWriter list = new(writer, Descriptor);

        list.WriteUInt(NextIncomingId);
        list.WriteUInt(IncomingWindow);
        list.WriteUInt(NextOutgoingId);
        list.WriteUInt(OutgoingWindow);
        list.WriteUInt(Handle);
        list.WriteUInt(DeliveryCount);
        list.WriteUInt(LinkCredit);
        list.WriteUInt(Available);
        list.WriteBoolean(Drain, false);
        list.WriteBoolean(Echo, false);
        list.WriteSymbolMap(Properties);
        list.Complete();
    }

    public static AmqpFlowPerformative Decode(ref AmqpReader fields, int count)
    {
        uint? nextIncomingId = count > 0 ? fields.ReadNullableUInt() : null;
        uint? incomingWindow = count > 1 ? fields.ReadNullableUInt() : null;
        uint? nextOutgoingId = count > 2 ? fields.ReadNullableUInt() : null;
        uint? outgoingWindow = count > 3 ? fields.ReadNullableUInt() : null;

        return new AmqpFlowPerformative
        {
            NextIncomingId = nextIncomingId,
            IncomingWindow = incomingWindow ?? throw MandatoryFieldMissing("flow", "incoming-window"),
            NextOutgoingId = nextOutgoingId ?? throw MandatoryFieldMissing("flow", "next-outgoing-id"),
            OutgoingWindow = outgoingWindow ?? throw MandatoryFieldMissing("flow", "outgoing-window"),
            Handle = count > 4 ? fields.ReadNullableUInt() : null,
            DeliveryCount = count > 5 ? fields.ReadNullableUInt() : null,
            LinkCredit = count > 6 ? fields.ReadNullableUInt() : null,
            Available = count > 7 ? fields.ReadNullableUInt() : null,
            Drain = count > 8 && fields.ReadBoolean(false),
            Echo = count > 9 && fields.ReadBoolean(false),
            Properties = count > 10 ? fields.ReadSymbolMap() : null
        };
    }
}
