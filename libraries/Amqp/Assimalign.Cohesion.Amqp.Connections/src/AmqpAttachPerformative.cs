using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Represents the AMQP attach performative.
/// </summary>
public sealed class AmqpAttachPerformative : AmqpPerformative
{
    /// <summary>
    /// Gets or sets the link name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the link handle.
    /// </summary>
    public uint Handle { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the link role is receiver.
    /// </summary>
    public bool Role { get; set; }

    /// <summary>
    /// Gets or sets the sender settlement mode.
    /// </summary>
    public AmqpSenderSettleMode? SenderSettleMode { get; set; }

    /// <summary>
    /// Gets or sets the receiver settlement mode.
    /// </summary>
    public AmqpReceiverSettleMode? ReceiverSettleMode { get; set; }

    /// <summary>
    /// Gets or sets the source terminus.
    /// </summary>
    public AmqpSource? Source { get; set; }

    /// <summary>
    /// Gets or sets the target terminus.
    /// </summary>
    public AmqpTarget? Target { get; set; }

    /// <summary>
    /// Gets or sets the unsettled delivery map.
    /// </summary>
    public IReadOnlyDictionary<ReadOnlyMemory<byte>, AmqpDescribedValue?>? Unsettled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the unsettled map is incomplete.
    /// </summary>
    public bool? IncompleteUnsettled { get; set; }

    /// <summary>
    /// Gets or sets the initial delivery count.
    /// </summary>
    public uint? InitialDeliveryCount { get; set; }

    /// <summary>
    /// Gets or sets the maximum message size.
    /// </summary>
    public ulong? MaxMessageSize { get; set; }

    /// <summary>
    /// Gets or sets the offered capabilities.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? OfferedCapabilities { get; set; }

    /// <summary>
    /// Gets or sets the desired capabilities.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? DesiredCapabilities { get; set; }

    /// <summary>
    /// Gets or sets the link properties.
    /// </summary>
    public IReadOnlyDictionary<AmqpSymbol, object?>? Properties { get; set; }
}
