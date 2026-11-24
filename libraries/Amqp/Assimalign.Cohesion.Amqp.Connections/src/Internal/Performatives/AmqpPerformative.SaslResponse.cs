using System;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The <c>sasl-response</c> frame: the client's answer to a challenge (AMQP 1.0 Part 5, section 5.3.3.4).
/// </summary>
internal sealed class AmqpSaslResponsePerformative : AmqpPerformative
{
    public override ulong Descriptor => AmqpDescriptor.SaslResponse;

    public ReadOnlyMemory<byte> Response { get; set; }

    public override void Encode(AmqpBufferWriter writer)
    {
        AmqpListWriter list = new(writer, Descriptor);

        list.WriteBinary(Response.Span);
        list.Complete();
    }

    public static AmqpSaslResponsePerformative Decode(ref AmqpReader fields, int count)
    {
        if (count < 1 || !fields.ReadBinary(out ReadOnlySpan<byte> response))
        {
            throw MandatoryFieldMissing("sasl-response", "response");
        }

        return new AmqpSaslResponsePerformative
        {
            Response = response.ToArray()
        };
    }
}
