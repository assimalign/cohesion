using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Represents the AMQP open performative.
/// </summary>
public sealed class AmqpOpenPerformative : AmqpPerformative
{
    /// <summary>
    /// Gets or sets the container identifier.
    /// </summary>
    public string ContainerId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target hostname.
    /// </summary>
    public string? HostName { get; set; }

    /// <summary>
    /// Gets or sets the maximum frame size.
    /// </summary>
    public uint? MaxFrameSize { get; set; }

    /// <summary>
    /// Gets or sets the maximum channel number.
    /// </summary>
    public ushort? ChannelMax { get; set; }

    /// <summary>
    /// Gets or sets the idle timeout in milliseconds.
    /// </summary>
    public uint? IdleTimeOut { get; set; }

    /// <summary>
    /// Gets or sets the outgoing locales.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? OutgoingLocales { get; set; }

    /// <summary>
    /// Gets or sets the incoming locales.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? IncomingLocales { get; set; }

    /// <summary>
    /// Gets or sets the offered capabilities.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? OfferedCapabilities { get; set; }

    /// <summary>
    /// Gets or sets the desired capabilities.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? DesiredCapabilities { get; set; }

    /// <summary>
    /// Gets or sets the connection properties.
    /// </summary>
    public IReadOnlyDictionary<AmqpSymbol, object?>? Properties { get; set; }
}
