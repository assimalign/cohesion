using System;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Represents the AMQP transfer performative.
/// </summary>
public sealed class AmqpTransferPerformative : AmqpPerformative
{
    /// <summary>
    /// Gets or sets the link handle.
    /// </summary>
    public uint Handle { get; set; }

    /// <summary>
    /// Gets or sets the delivery identifier.
    /// </summary>
    public uint? DeliveryId { get; set; }

    /// <summary>
    /// Gets or sets the delivery tag.
    /// </summary>
    public ReadOnlyMemory<byte>? DeliveryTag { get; set; }

    /// <summary>
    /// Gets or sets the message format.
    /// </summary>
    public uint? MessageFormat { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the transfer is settled.
    /// </summary>
    public bool? Settled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether additional transfer frames follow.
    /// </summary>
    public bool? More { get; set; }

    /// <summary>
    /// Gets or sets the receiver settlement mode.
    /// </summary>
    public AmqpReceiverSettleMode? ReceiverSettleMode { get; set; }

    /// <summary>
    /// Gets or sets the delivery state.
    /// </summary>
    public AmqpDescribedValue? State { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the transfer is resumed.
    /// </summary>
    public bool? Resume { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the transfer is aborted.
    /// </summary>
    public bool? Aborted { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the transfer is batchable.
    /// </summary>
    public bool? Batchable { get; set; }
}
