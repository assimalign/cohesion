namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the <c>accepted</c> outcome: the receiver processed the message successfully (AMQP 1.0
/// Part 3, section 3.4.2).
/// </summary>
public sealed class AmqpAccepted : AmqpOutcome
{
    /// <summary>
    /// Initializes a new accepted outcome. <see cref="Instance"/> avoids the allocation.
    /// </summary>
    public AmqpAccepted()
    {
    }

    /// <summary>
    /// Gets a shared accepted outcome. The outcome carries no state, so every instance is equivalent.
    /// </summary>
    public static AmqpAccepted Instance { get; } = new();

    /// <summary>
    /// Returns the outcome's name.
    /// </summary>
    /// <returns>The text <c>accepted</c>.</returns>
    public override string ToString()
    {
        return "accepted";
    }
}
