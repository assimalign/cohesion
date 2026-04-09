namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Represents the AMQP end performative.
/// </summary>
public sealed class AmqpEndPerformative : AmqpPerformative
{
    /// <summary>
    /// Gets or sets the session error.
    /// </summary>
    public AmqpError? Error { get; set; }
}
