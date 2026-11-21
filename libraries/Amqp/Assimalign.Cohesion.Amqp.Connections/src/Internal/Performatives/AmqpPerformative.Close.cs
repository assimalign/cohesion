using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The <c>close</c> performative: closes the connection (AMQP 1.0 Part 2, section 2.7.9).
/// </summary>
internal sealed class AmqpClosePerformative : AmqpPerformative
{
    public override ulong Descriptor => AmqpDescriptor.Close;

    public AmqpError? Error { get; set; }

    public override void Encode(AmqpBufferWriter writer)
    {
        AmqpListWriter list = new(writer, Descriptor);

        AmqpTypeCodec.WriteError(writer, Error);
        list.Field(Error is null);
        list.Complete();
    }

    public static AmqpClosePerformative Decode(ref AmqpReader fields, int count)
    {
        return new AmqpClosePerformative
        {
            Error = count > 0 ? AmqpTypeCodec.ReadError(ref fields) : null
        };
    }
}
