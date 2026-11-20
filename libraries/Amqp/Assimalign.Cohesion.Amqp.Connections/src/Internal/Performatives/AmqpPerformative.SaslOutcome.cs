using System;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The <c>sasl-outcome</c> frame: the result of the authentication exchange (AMQP 1.0 Part 5, section 5.3.3.5).
/// </summary>
internal sealed class AmqpSaslOutcomePerformative : AmqpPerformative
{
    public override ulong Descriptor => AmqpDescriptor.SaslOutcome;

    public AmqpSaslCode Code { get; set; }

    public ReadOnlyMemory<byte>? AdditionalData { get; set; }

    public override void Encode(AmqpBufferWriter writer)
    {
        AmqpListWriter list = new(writer, Descriptor);

        // The code is mandatory, so ok (0) is written rather than left to a default.
        AmqpEncoder.WriteUByte(writer, (byte)Code);
        list.Field(false);
        list.WriteBinary(AdditionalData);
        list.Complete();
    }

    public static AmqpSaslOutcomePerformative Decode(ref AmqpReader fields, int count)
    {
        byte? code = count > 0 ? fields.ReadNullableUByte() : null;
        AmqpSaslOutcomePerformative outcome = new()
        {
            Code = (AmqpSaslCode)(code ?? throw MandatoryFieldMissing("sasl-outcome", "code"))
        };

        if (count > 1 && fields.ReadBinary(out ReadOnlySpan<byte> additionalData))
        {
            outcome.AdditionalData = additionalData.ToArray();
        }

        return outcome;
    }
}
