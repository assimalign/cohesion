using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents the source of a link: the terminus a link's messages originate from (AMQP 1.0 Part 3,
/// section 3.5.3).
/// </summary>
/// <remarks>
/// The sending endpoint of a link is authoritative for the source. A receiver proposes the source it
/// wants, and the sender's attach carries the source it actually uses; a sender that refuses the link
/// attaches with no source and detaches.
/// </remarks>
public sealed class AmqpSource
{
    /// <summary>
    /// Gets or sets the address of the source node, or <see langword="null"/> when the source is dynamic
    /// and the sender has not yet assigned one.
    /// </summary>
    public string? Address { get; set; }

    /// <summary>
    /// Gets or sets what state of the source is retained durably. Defaults to <see cref="AmqpTerminusDurability.None"/>.
    /// </summary>
    public AmqpTerminusDurability Durable { get; set; }

    /// <summary>
    /// Gets or sets the event that starts the source's expiry timer. Defaults to
    /// <see cref="AmqpTerminusExpiryPolicy.SessionEnd"/>, which the wire also encodes by omission.
    /// </summary>
    public AmqpSymbol ExpiryPolicy { get; set; } = AmqpTerminusExpiryPolicy.SessionEnd;

    /// <summary>
    /// Gets or sets how long, in seconds, the source survives once its expiry timer starts. Defaults to 0.
    /// </summary>
    public uint Timeout { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the receiver asks the sender to create the source node on
    /// demand and assign its address.
    /// </summary>
    public bool Dynamic { get; set; }

    /// <summary>
    /// Gets or sets the properties of the dynamically created node, or <see langword="null"/> for none.
    /// </summary>
    public IReadOnlyDictionary<AmqpSymbol, object?>? DynamicNodeProperties { get; set; }

    /// <summary>
    /// Gets or sets the distribution mode, such as <see cref="AmqpDistributionMode.Move"/>, or
    /// <see langword="null"/> to leave it to the node.
    /// </summary>
    public AmqpSymbol? DistributionMode { get; set; }

    /// <summary>
    /// Gets or sets the filters the source applies, keyed by filter name, or <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// Each value is a described filter, normally an <see cref="AmqpDescribedValue"/>, or
    /// <see langword="null"/>.
    /// </remarks>
    public IReadOnlyDictionary<AmqpSymbol, object?>? Filter { get; set; }

    /// <summary>
    /// Gets or sets the outcome a delivery is given when it is settled without one, or
    /// <see langword="null"/> to leave it to the node.
    /// </summary>
    public AmqpOutcome? DefaultOutcome { get; set; }

    /// <summary>
    /// Gets or sets the descriptor names of the outcomes the source supports, or <see langword="null"/>
    /// for the node's defaults.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? Outcomes { get; set; }

    /// <summary>
    /// Gets or sets the extension capabilities the source supports or desires, or <see langword="null"/> for none.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? Capabilities { get; set; }
}
