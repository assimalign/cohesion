using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Represents the AMQP begin performative.
/// </summary>
public sealed class AmqpBeginPerformative : AmqpPerformative
{
    /// <summary>
    /// Gets or sets the remote channel.
    /// </summary>
    public ushort? RemoteChannel { get; set; }

    /// <summary>
    /// Gets or sets the next outgoing transfer id.
    /// </summary>
    public uint NextOutgoingId { get; set; }

    /// <summary>
    /// Gets or sets the incoming window.
    /// </summary>
    public uint IncomingWindow { get; set; }

    /// <summary>
    /// Gets or sets the outgoing window.
    /// </summary>
    public uint OutgoingWindow { get; set; }

    /// <summary>
    /// Gets or sets the maximum handle number.
    /// </summary>
    public uint? HandleMax { get; set; }

    /// <summary>
    /// Gets or sets the offered capabilities.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? OfferedCapabilities { get; set; }

    /// <summary>
    /// Gets or sets the desired capabilities.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? DesiredCapabilities { get; set; }

    /// <summary>
    /// Gets or sets the session properties.
    /// </summary>
    public IReadOnlyDictionary<AmqpSymbol, object?>? Properties { get; set; }
}
