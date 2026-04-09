namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Represents the AMQP disposition performative.
/// </summary>
public sealed class AmqpDispositionPerformative : AmqpPerformative
{
    /// <summary>
    /// Gets or sets a value indicating whether the role is receiver.
    /// </summary>
    public bool Role { get; set; }

    /// <summary>
    /// Gets or sets the first delivery id.
    /// </summary>
    public uint First { get; set; }

    /// <summary>
    /// Gets or sets the last delivery id.
    /// </summary>
    public uint? Last { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the delivery is settled.
    /// </summary>
    public bool? Settled { get; set; }

    /// <summary>
    /// Gets or sets the delivery state.
    /// </summary>
    public AmqpDescribedValue? State { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the disposition is batchable.
    /// </summary>
    public bool? Batchable { get; set; }
}
