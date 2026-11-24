using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The <c>detach</c> performative: detaches a link, and closes it when <see cref="Closed"/> is set
/// (AMQP 1.0 Part 2, section 2.7.7).
/// </summary>
internal sealed class AmqpDetachPerformative : AmqpPerformative
{
    public override ulong Descriptor => AmqpDescriptor.Detach;

    public uint Handle { get; set; }

    public bool Closed { get; set; }

    public AmqpError? Error { get; set; }

    public override void Encode(AmqpBufferWriter writer)
    {
        AmqpListWriter list = new(writer, Descriptor);

        list.WriteUInt(Handle);
        list.WriteBoolean(Closed, false);
        AmqpTypeCodec.WriteError(writer, Error);
        list.Field(Error is null);
        list.Complete();
    }

    public static AmqpDetachPerformative Decode(ref AmqpReader fields, int count)
    {
        uint? handle = count > 0 ? fields.ReadNullableUInt() : null;

        return new AmqpDetachPerformative
        {
            Handle = handle ?? throw MandatoryFieldMissing("detach", "handle"),
            Closed = count > 1 && fields.ReadBoolean(false),
            Error = count > 2 ? AmqpTypeCodec.ReadError(ref fields) : null
        };
    }
}
