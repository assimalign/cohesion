using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The <c>disposition</c> performative: informs the peer of a range of deliveries' state and settlement
/// (AMQP 1.0 Part 2, section 2.7.6).
/// </summary>
internal sealed class AmqpDispositionPerformative : AmqpPerformative
{
    public override ulong Descriptor => AmqpDescriptor.Disposition;

    /// <summary>
    /// Gets or sets the role of the endpoint that sends the disposition.
    /// </summary>
    public AmqpRole Role { get; set; }

    public uint First { get; set; }

    public uint? Last { get; set; }

    public bool Settled { get; set; }

    public AmqpDeliveryState? State { get; set; }

    public bool Batchable { get; set; }

    public override void Encode(AmqpBufferWriter writer)
    {
        AmqpListWriter list = new(writer, Descriptor);

        list.WriteBoolean(Role == AmqpRole.Receiver);
        list.WriteUInt(First);

        // A range of one is sent without its last delivery id, which defaults to the first.
        list.WriteUInt(Last == First ? null : Last);
        list.WriteBoolean(Settled, false);
        AmqpTypeCodec.WriteDeliveryState(writer, State);
        list.Field(State is null);
        list.WriteBoolean(Batchable, false);
        list.Complete();
    }

    public static AmqpDispositionPerformative Decode(ref AmqpReader fields, int count)
    {
        bool? role = count > 0 ? fields.ReadNullableBoolean() : null;
        uint? first = count > 1 ? fields.ReadNullableUInt() : null;

        return new AmqpDispositionPerformative
        {
            Role = (role ?? throw MandatoryFieldMissing("disposition", "role")) ? AmqpRole.Receiver : AmqpRole.Sender,
            First = first ?? throw MandatoryFieldMissing("disposition", "first"),
            Last = count > 2 ? fields.ReadNullableUInt() : null,
            Settled = count > 3 && fields.ReadBoolean(false),
            State = count > 4 ? AmqpTypeCodec.ReadDeliveryState(ref fields) : null,
            Batchable = count > 5 && fields.ReadBoolean(false)
        };
    }
}
