namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the <c>declare</c> message body: a request to the transaction coordinator to allocate a
/// transaction (AMQP 1.0 Part 4, section 4.5.2).
/// </summary>
/// <remarks>
/// It travels as the value of an <see cref="AmqpValueBody"/> on a coordinator link, and the coordinator
/// answers with an <see cref="AmqpDeclared"/> outcome, or an <see cref="AmqpRejected"/> one.
/// </remarks>
public sealed class AmqpDeclare
{
    /// <summary>
    /// Initializes a new declare for a local transaction.
    /// </summary>
    public AmqpDeclare()
    {
    }

    /// <summary>
    /// Gets or sets the global identifier of a distributed transaction to associate, or
    /// <see langword="null"/> for a local transaction.
    /// </summary>
    public object? GlobalId { get; set; }
}
