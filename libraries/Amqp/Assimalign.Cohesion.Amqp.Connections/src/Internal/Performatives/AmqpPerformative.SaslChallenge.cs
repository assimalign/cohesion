using System;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The <c>sasl-challenge</c> frame: a challenge the server sends the client (AMQP 1.0 Part 5, section 5.3.3.3).
/// </summary>
internal sealed class AmqpSaslChallengePerformative : AmqpPerformative
{
    public override ulong Descriptor => AmqpDescriptor.SaslChallenge;

    public ReadOnlyMemory<byte> Challenge { get; set; }

    public override void Encode(AmqpBufferWriter writer)
    {
        AmqpListWriter list = new(writer, Descriptor);

        list.WriteBinary(Challenge.Span);
        list.Complete();
    }

    public static AmqpSaslChallengePerformative Decode(ref AmqpReader fields, int count)
    {
        if (count < 1 || !fields.ReadBinary(out ReadOnlySpan<byte> challenge))
        {
            throw MandatoryFieldMissing("sasl-challenge", "challenge");
        }

        return new AmqpSaslChallengePerformative
        {
            Challenge = challenge.ToArray()
        };
    }
}
