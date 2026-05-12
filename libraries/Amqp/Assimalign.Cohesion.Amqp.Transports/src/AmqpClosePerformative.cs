namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Represents the AMQP close performative.
/// </summary>
public sealed class AmqpClosePerformative : AmqpPerformative
{
    /// <summary>
    /// Gets or sets the connection error.
    /// </summary>
    public AmqpError? Error { get; set; }
}
