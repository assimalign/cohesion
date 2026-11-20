using System.Globalization;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The body of an AMQP or SASL frame: a described list whose descriptor names the operation (AMQP 1.0
/// Part 2, section 2.7; Part 5, section 5.3.3).
/// </summary>
internal abstract class AmqpPerformative
{
    private protected AmqpPerformative()
    {
    }

    /// <summary>
    /// Gets the numeric descriptor of the performative.
    /// </summary>
    public abstract ulong Descriptor { get; }

    /// <summary>
    /// Gets the performative's name as the specification spells it, for diagnostics.
    /// </summary>
    public string PerformativeName => Descriptor switch
    {
        AmqpDescriptor.Open => "open",
        AmqpDescriptor.Begin => "begin",
        AmqpDescriptor.Attach => "attach",
        AmqpDescriptor.Flow => "flow",
        AmqpDescriptor.Transfer => "transfer",
        AmqpDescriptor.Disposition => "disposition",
        AmqpDescriptor.Detach => "detach",
        AmqpDescriptor.End => "end",
        AmqpDescriptor.Close => "close",
        AmqpDescriptor.SaslMechanisms => "sasl-mechanisms",
        AmqpDescriptor.SaslInit => "sasl-init",
        AmqpDescriptor.SaslChallenge => "sasl-challenge",
        AmqpDescriptor.SaslResponse => "sasl-response",
        AmqpDescriptor.SaslOutcome => "sasl-outcome",
        _ => Descriptor.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>
    /// Writes the performative as a described list.
    /// </summary>
    public abstract void Encode(AmqpBufferWriter writer);

    /// <summary>
    /// Reads the performative at the start of a frame body, leaving the reader at the payload that
    /// follows a <c>transfer</c>.
    /// </summary>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.ProtocolViolation"/> when the body is not a performative this library knows.</exception>
    public static AmqpPerformative Decode(ref AmqpReader reader)
    {
        if (!reader.TryReadDescriptor(out ulong? code, out object? descriptor))
        {
            throw AmqpException.DecodeError(ErrorMessages.PerformativeNotDescribed);
        }

        AmqpReader fields = reader.ReadList(out int count);

        return code switch
        {
            AmqpDescriptor.Open => AmqpOpenPerformative.Decode(ref fields, count),
            AmqpDescriptor.Begin => AmqpBeginPerformative.Decode(ref fields, count),
            AmqpDescriptor.Attach => AmqpAttachPerformative.Decode(ref fields, count),
            AmqpDescriptor.Flow => AmqpFlowPerformative.Decode(ref fields, count),
            AmqpDescriptor.Transfer => AmqpTransferPerformative.Decode(ref fields, count),
            AmqpDescriptor.Disposition => AmqpDispositionPerformative.Decode(ref fields, count),
            AmqpDescriptor.Detach => AmqpDetachPerformative.Decode(ref fields, count),
            AmqpDescriptor.End => AmqpEndPerformative.Decode(ref fields, count),
            AmqpDescriptor.Close => AmqpClosePerformative.Decode(ref fields, count),
            AmqpDescriptor.SaslMechanisms => AmqpSaslMechanismsPerformative.Decode(ref fields, count),
            AmqpDescriptor.SaslInit => AmqpSaslInitPerformative.Decode(ref fields, count),
            AmqpDescriptor.SaslChallenge => AmqpSaslChallengePerformative.Decode(ref fields, count),
            AmqpDescriptor.SaslResponse => AmqpSaslResponsePerformative.Decode(ref fields, count),
            AmqpDescriptor.SaslOutcome => AmqpSaslOutcomePerformative.Decode(ref fields, count),
            _ => throw AmqpException.ProtocolViolation(
                AmqpErrorCondition.NotImplemented,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.PerformativeUnknown, descriptor))
        };
    }

    /// <summary>
    /// Creates the exception for a performative that arrived without a field AMQP 1.0 makes mandatory.
    /// </summary>
    protected static AmqpException MandatoryFieldMissing(string performative, string field)
    {
        return AmqpTypeCodec.MandatoryFieldMissing(performative, field);
    }
}
