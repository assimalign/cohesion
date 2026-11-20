namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents a body of a single <c>amqp-value</c> section: one AMQP value of any type (AMQP 1.0 Part 3,
/// section 3.2.8).
/// </summary>
public sealed class AmqpValueBody : AmqpMessageBody
{
    /// <summary>
    /// Initializes a body of one value section.
    /// </summary>
    /// <param name="value">The value, which may be <see langword="null"/>.</param>
    public AmqpValueBody(object? value)
    {
        Value = value;
    }

    /// <summary>
    /// Gets the value, which may be <see langword="null"/>.
    /// </summary>
    public object? Value { get; }
}
