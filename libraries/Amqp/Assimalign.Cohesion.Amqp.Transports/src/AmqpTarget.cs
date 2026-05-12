using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Represents an AMQP target terminus.
/// </summary>
public sealed class AmqpTarget
{
    /// <summary>
    /// Gets or sets the target address.
    /// </summary>
    public string? Address { get; set; }

    /// <summary>
    /// Gets or sets the target durability mode.
    /// </summary>
    public uint? Durable { get; set; }

    /// <summary>
    /// Gets or sets the target expiry policy symbol.
    /// </summary>
    public AmqpSymbol? ExpiryPolicy { get; set; }

    /// <summary>
    /// Gets or sets the target timeout in seconds.
    /// </summary>
    public uint? Timeout { get; set; }

    /// <summary>
    /// Gets or sets whether the target is dynamic.
    /// </summary>
    public bool? Dynamic { get; set; }

    /// <summary>
    /// Gets or sets the dynamic node properties.
    /// </summary>
    public IReadOnlyDictionary<AmqpSymbol, object?>? DynamicNodeProperties { get; set; }

    /// <summary>
    /// Gets or sets the target capabilities.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? Capabilities { get; set; }
}
