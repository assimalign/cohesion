using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Represents the AMQP flow performative.
/// </summary>
public sealed class AmqpFlowPerformative : AmqpPerformative
{
    /// <summary>
    /// Gets or sets the next incoming transfer id.
    /// </summary>
    public uint? NextIncomingId { get; set; }

    /// <summary>
    /// Gets or sets the incoming window.
    /// </summary>
    public uint IncomingWindow { get; set; }

    /// <summary>
    /// Gets or sets the next outgoing transfer id.
    /// </summary>
    public uint NextOutgoingId { get; set; }

    /// <summary>
    /// Gets or sets the outgoing window.
    /// </summary>
    public uint OutgoingWindow { get; set; }

    /// <summary>
    /// Gets or sets the link handle.
    /// </summary>
    public uint? Handle { get; set; }

    /// <summary>
    /// Gets or sets the delivery count.
    /// </summary>
    public uint? DeliveryCount { get; set; }

    /// <summary>
    /// Gets or sets the link credit.
    /// </summary>
    public uint? LinkCredit { get; set; }

    /// <summary>
    /// Gets or sets the available message count.
    /// </summary>
    public uint? Available { get; set; }

    /// <summary>
    /// Gets or sets the drain flag.
    /// </summary>
    public bool? Drain { get; set; }

    /// <summary>
    /// Gets or sets the echo flag.
    /// </summary>
    public bool? Echo { get; set; }

    /// <summary>
    /// Gets or sets the flow properties.
    /// </summary>
    public IReadOnlyDictionary<AmqpSymbol, object?>? Properties { get; set; }
}
