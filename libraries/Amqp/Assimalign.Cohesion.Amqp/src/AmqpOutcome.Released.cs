namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the <c>released</c> outcome: the receiver did not process the message, which the source
/// may redeliver to any receiver (AMQP 1.0 Part 3, section 3.4.4).
/// </summary>
public sealed class AmqpReleased : AmqpOutcome
{
    /// <summary>
    /// Gets the outcome's symbolic descriptor, <c>amqp:released:list</c>, the name a source lists in
    /// <see cref="AmqpSource.Outcomes"/> to say a receiver may choose it.
    /// </summary>
    public static AmqpSymbol Descriptor { get; } = new("amqp:released:list");

    /// <summary>
    /// Initializes a new released outcome. <see cref="Instance"/> avoids the allocation.
    /// </summary>
    public AmqpReleased()
    {
    }

    /// <summary>
    /// Gets a shared released outcome. The outcome carries no state, so every instance is equivalent.
    /// </summary>
    public static AmqpReleased Instance { get; } = new();

    /// <summary>
    /// Returns the outcome's name.
    /// </summary>
    /// <returns>The text <c>released</c>.</returns>
    public override string ToString()
    {
        return "released";
    }
}
