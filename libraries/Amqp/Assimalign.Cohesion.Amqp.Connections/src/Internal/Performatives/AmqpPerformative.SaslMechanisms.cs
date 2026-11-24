using System.Collections.Generic;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The <c>sasl-mechanisms</c> frame: the mechanisms the server supports (AMQP 1.0 Part 5, section 5.3.3.1).
/// </summary>
internal sealed class AmqpSaslMechanismsPerformative : AmqpPerformative
{
    public override ulong Descriptor => AmqpDescriptor.SaslMechanisms;

    public IReadOnlyList<AmqpSymbol> Mechanisms { get; set; } = [];

    public override void Encode(AmqpBufferWriter writer)
    {
        AmqpListWriter list = new(writer, Descriptor);

        // The field is mandatory, so even an empty set is written, as an empty array.
        AmqpEncoder.WriteSymbolArray(writer, Mechanisms);
        list.Field(false);
        list.Complete();
    }

    public static AmqpSaslMechanismsPerformative Decode(ref AmqpReader fields, int count)
    {
        AmqpSymbol[]? mechanisms = count > 0 ? fields.ReadSymbolMultiple() : null;

        return new AmqpSaslMechanismsPerformative
        {
            Mechanisms = mechanisms ?? throw MandatoryFieldMissing("sasl-mechanisms", "sasl-server-mechanisms")
        };
    }
}
