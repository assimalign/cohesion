using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Represents an AMQP source terminus.
/// </summary>
public sealed class AmqpSource
{
    /// <summary>
    /// Gets or sets the source address.
    /// </summary>
    public string? Address { get; set; }

    /// <summary>
    /// Gets or sets the source durability mode.
    /// </summary>
    public uint? Durable { get; set; }

    /// <summary>
    /// Gets or sets the source expiry policy symbol.
    /// </summary>
    public AmqpSymbol? ExpiryPolicy { get; set; }

    /// <summary>
    /// Gets or sets the source timeout in seconds.
    /// </summary>
    public uint? Timeout { get; set; }

    /// <summary>
    /// Gets or sets whether the source is dynamic.
    /// </summary>
    public bool? Dynamic { get; set; }

    /// <summary>
    /// Gets or sets the dynamic node properties.
    /// </summary>
    public IReadOnlyDictionary<AmqpSymbol, object?>? DynamicNodeProperties { get; set; }

    /// <summary>
    /// Gets or sets the distribution mode symbol.
    /// </summary>
    public AmqpSymbol? DistributionMode { get; set; }

    /// <summary>
    /// Gets or sets the filter set.
    /// </summary>
    public IReadOnlyDictionary<AmqpSymbol, object?>? Filter { get; set; }

    /// <summary>
    /// Gets or sets the default outcome.
    /// </summary>
    public AmqpDescribedValue? DefaultOutcome { get; set; }

    /// <summary>
    /// Gets or sets the supported outcomes.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? Outcomes { get; set; }

    /// <summary>
    /// Gets or sets the supported capabilities.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? Capabilities { get; set; }
}
