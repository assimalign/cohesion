using System;
using System.Linq;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// One step of a header-phase script: what the scripted peer writes, or what it expects the engine under
/// test to write next.
/// </summary>
/// <param name="Kind">What the step does.</param>
/// <param name="Bytes">The bytes the peer writes, or the protocol header it expects.</param>
/// <param name="Performative">The name of the performative the peer expects in the engine's next frame.</param>
/// <param name="Condition">The condition the engine's <c>close</c> carries, or <see langword="null"/> for none.</param>
/// <param name="Description">The description the engine's <c>close</c> carries, or <see langword="null"/> to leave it unchecked.</param>
/// <param name="Duration">How long the peer waits for an empty frame, or listens for silence.</param>
/// <param name="Verify">Checks the performative the peer read, or <see langword="null"/>.</param>
internal sealed record AmqpHeaderPhaseStep(
    AmqpHeaderPhaseStepKind Kind,
    byte[]? Bytes = null,
    string? Performative = null,
    AmqpSymbol? Condition = null,
    string? Description = null,
    TimeSpan Duration = default,
    Action<AmqpPerformative>? Verify = null)
{
    /// <summary>
    /// The peer writes the parts, back to back, in one write.
    /// </summary>
    public static AmqpHeaderPhaseStep Write(params byte[][] parts)
    {
        return new AmqpHeaderPhaseStep(AmqpHeaderPhaseStepKind.Write, Bytes: parts.SelectMany(part => part).ToArray());
    }

    /// <summary>
    /// The peer reads the engine's protocol header and expects <paramref name="header"/>.
    /// </summary>
    public static AmqpHeaderPhaseStep ExpectHeader(byte[] header)
    {
        return new AmqpHeaderPhaseStep(AmqpHeaderPhaseStepKind.ExpectHeader, Bytes: header);
    }

    /// <summary>
    /// The peer reads the engine's next frame with a body and expects the performative, in a SASL frame for
    /// a SASL performative and an AMQP frame otherwise.
    /// </summary>
    public static AmqpHeaderPhaseStep ExpectFrame(string performative, Action<AmqpPerformative>? verify = null)
    {
        return new AmqpHeaderPhaseStep(AmqpHeaderPhaseStepKind.ExpectFrame, Performative: performative, Verify: verify);
    }

    /// <summary>
    /// The peer expects the engine's next frame, within <paramref name="within"/>, to be empty and on channel 0.
    /// </summary>
    public static AmqpHeaderPhaseStep ExpectEmptyFrame(TimeSpan within)
    {
        return new AmqpHeaderPhaseStep(AmqpHeaderPhaseStepKind.ExpectEmptyFrame, Duration: within);
    }

    /// <summary>
    /// The peer reads past empty frames to the engine's <c>close</c> and expects its error.
    /// </summary>
    public static AmqpHeaderPhaseStep ExpectClose(AmqpSymbol? condition, string? description = null)
    {
        return new AmqpHeaderPhaseStep(AmqpHeaderPhaseStepKind.ExpectClose, Condition: condition, Description: description);
    }

    /// <summary>
    /// The peer expects no frame at all, not even an empty one, for <paramref name="duration"/>.
    /// </summary>
    public static AmqpHeaderPhaseStep ExpectSilence(TimeSpan duration)
    {
        return new AmqpHeaderPhaseStep(AmqpHeaderPhaseStepKind.ExpectSilence, Duration: duration);
    }

    /// <summary>
    /// The peer expects the engine to hang up without writing another frame, empty or not.
    /// </summary>
    public static AmqpHeaderPhaseStep ExpectEnd()
    {
        return new AmqpHeaderPhaseStep(AmqpHeaderPhaseStepKind.ExpectEnd);
    }

    /// <summary>
    /// The peer begins a session on channel 0 and expects the engine's answering <c>begin</c>, which shows
    /// the connection still works.
    /// </summary>
    public static AmqpHeaderPhaseStep ExpectAlive()
    {
        return new AmqpHeaderPhaseStep(AmqpHeaderPhaseStepKind.ExpectAlive);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Kind switch
        {
            AmqpHeaderPhaseStepKind.Write => $"write {Convert.ToHexString(Bytes!)}",
            AmqpHeaderPhaseStepKind.ExpectHeader => $"expect header {Convert.ToHexString(Bytes!)}",
            AmqpHeaderPhaseStepKind.ExpectFrame => $"expect '{Performative}'",
            AmqpHeaderPhaseStepKind.ExpectClose => $"expect close with '{Condition}'",
            AmqpHeaderPhaseStepKind.ExpectEmptyFrame => $"expect an empty frame within {Duration.TotalMilliseconds} ms",
            AmqpHeaderPhaseStepKind.ExpectSilence => $"expect silence for {Duration.TotalMilliseconds} ms",
            _ => Kind.ToString()
        };
    }
}

/// <summary>
/// What a <see cref="AmqpHeaderPhaseStep"/> does.
/// </summary>
internal enum AmqpHeaderPhaseStepKind
{
    Write,
    ExpectHeader,
    ExpectFrame,
    ExpectEmptyFrame,
    ExpectClose,
    ExpectSilence,
    ExpectEnd,
    ExpectAlive
}
