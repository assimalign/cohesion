namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the <c>rejected</c> outcome: the receiver found the message invalid and will not process
/// it (AMQP 1.0 Part 3, section 3.4.3).
/// </summary>
public sealed class AmqpRejected : AmqpOutcome
{
    /// <summary>
    /// Gets the outcome's symbolic descriptor, <c>amqp:rejected:list</c>, the name a source lists in
    /// <see cref="AmqpSource.Outcomes"/> to say a receiver may choose it.
    /// </summary>
    public static AmqpSymbol Descriptor { get; } = new("amqp:rejected:list");

    /// <summary>
    /// Initializes a new rejected outcome.
    /// </summary>
    /// <param name="error">The reason the message was rejected, or <see langword="null"/>.</param>
    public AmqpRejected(AmqpError? error = null)
    {
        Error = error;
    }

    /// <summary>
    /// Gets the reason the message was rejected, or <see langword="null"/>.
    /// </summary>
    public AmqpError? Error { get; }

    /// <summary>
    /// Returns the outcome's name, followed by its error when there is one.
    /// </summary>
    /// <returns>The outcome's name and error.</returns>
    public override string ToString()
    {
        return Error is null ? "rejected" : "rejected (" + Error + ")";
    }
}
