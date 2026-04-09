namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Represents the AMQP detach performative.
/// </summary>
public sealed class AmqpDetachPerformative : AmqpPerformative
{
    /// <summary>
    /// Gets or sets the link handle.
    /// </summary>
    public uint Handle { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the detach closes the link.
    /// </summary>
    public bool? Closed { get; set; }

    /// <summary>
    /// Gets or sets the detach error.
    /// </summary>
    public AmqpError? Error { get; set; }
}
