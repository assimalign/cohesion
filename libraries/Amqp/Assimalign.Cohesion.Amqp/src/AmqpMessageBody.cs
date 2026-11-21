namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the body of a message (AMQP 1.0 Part 3, section 3.2): one or more data sections, one or
/// more sequence sections, or a single value section.
/// </summary>
/// <remarks>
/// The three shapes are exclusive, which the closed hierarchy states: a body is an
/// <see cref="AmqpDataBody"/>, an <see cref="AmqpSequenceBody"/> or an <see cref="AmqpValueBody"/>.
/// </remarks>
public abstract class AmqpMessageBody
{
    private protected AmqpMessageBody()
    {
    }
}
