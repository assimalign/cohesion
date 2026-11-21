namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Provides the distribution modes AMQP 1.0 defines for a source (Part 3, section 3.5.7).
/// </summary>
public static class AmqpDistributionMode
{
    /// <summary>
    /// Gets the mode under which a message is moved to the link, so no other link receives it (<c>move</c>).
    /// </summary>
    public static AmqpSymbol Move { get; } = new("move");

    /// <summary>
    /// Gets the mode under which a copy of a message is sent, so other links can also receive it (<c>copy</c>).
    /// </summary>
    public static AmqpSymbol Copy { get; } = new("copy");
}
