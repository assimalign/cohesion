using System;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// How establishment ends in a header-phase case: the connection opens, or establishment fails with an
/// exact exception.
/// </summary>
/// <param name="Opened"><see langword="true"/> when the listener accepts or the factory opens the connection.</param>
/// <param name="ErrorCode">The code of the failure a factory throws.</param>
/// <param name="Condition">The AMQP condition the failure carries, or <see langword="null"/> when it carries none.</param>
/// <param name="Message">The failure's message, which a listener reports through its failure event.</param>
/// <param name="Verify">Checks the opened connection through its public surface, or <see langword="null"/>.</param>
internal sealed record AmqpHeaderPhaseOutcome(
    bool Opened,
    AmqpErrorCode ErrorCode,
    AmqpSymbol? Condition,
    string? Message,
    Action<IAmqpConnection>? Verify)
{
    /// <summary>
    /// The connection opens, and <paramref name="verify"/>, when given, holds for it.
    /// </summary>
    public static AmqpHeaderPhaseOutcome Open(Action<IAmqpConnection>? verify = null)
    {
        return new AmqpHeaderPhaseOutcome(true, AmqpErrorCode.Other, null, null, verify);
    }

    /// <summary>
    /// Establishment fails with an <see cref="AmqpException"/> of <paramref name="errorCode"/> and
    /// <paramref name="message"/>, carrying <paramref name="condition"/>.
    /// </summary>
    public static AmqpHeaderPhaseOutcome Fail(AmqpErrorCode errorCode, string message, AmqpSymbol? condition = null)
    {
        return new AmqpHeaderPhaseOutcome(false, errorCode, condition, message, null);
    }
}
