using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The <c>end</c> performative: ends a session (AMQP 1.0 Part 2, section 2.7.8).
/// </summary>
internal sealed class AmqpEndPerformative : AmqpPerformative
{
    public override ulong Descriptor => AmqpDescriptor.End;

    public AmqpError? Error { get; set; }

    public override void Encode(AmqpBufferWriter writer)
    {
        AmqpListWriter list = new(writer, Descriptor);

        AmqpTypeCodec.WriteError(writer, Error);
        list.Field(Error is null);
        list.Complete();
    }

    public static AmqpEndPerformative Decode(ref AmqpReader fields, int count)
    {
        return new AmqpEndPerformative
        {
            Error = count > 0 ? AmqpTypeCodec.ReadError(ref fields) : null
        };
    }
}
