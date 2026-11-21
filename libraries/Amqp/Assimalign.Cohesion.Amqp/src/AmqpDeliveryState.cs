namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the state of a delivery at one of its endpoints (AMQP 1.0 Part 2, section 2.6.13): a
/// terminal <see cref="AmqpOutcome"/>, the non-terminal <see cref="AmqpReceived"/> progress state, or the
/// <see cref="AmqpTransactionalState"/> of work done under a transaction.
/// </summary>
/// <remarks>
/// The hierarchy is closed: only this library defines delivery states, so the codec can encode every
/// instance. A delivery state with a descriptor the library does not know is a decode error.
/// </remarks>
public abstract class AmqpDeliveryState
{
    private protected AmqpDeliveryState()
    {
    }
}
