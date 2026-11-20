namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents a terminal delivery state: the outcome a receiver gives a delivery (AMQP 1.0 Part 3,
/// section 3.4).
/// </summary>
/// <remarks>
/// The messaging outcomes are <see cref="AmqpAccepted"/>, <see cref="AmqpRejected"/>, <see cref="AmqpReleased"/>
/// and <see cref="AmqpModified"/>; a transaction coordinator answers a declare with <see cref="AmqpDeclared"/>.
/// The hierarchy is closed, like <see cref="AmqpDeliveryState"/>.
/// </remarks>
public abstract class AmqpOutcome : AmqpDeliveryState
{
    private protected AmqpOutcome()
    {
    }
}
