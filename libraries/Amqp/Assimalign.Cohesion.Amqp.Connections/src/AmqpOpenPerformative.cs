using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Represents the AMQP open performative.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("AmqpOpenPerformative")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class AmqpOpenPerformative : AmqpPerformative
{
    private uint? _maxFrameSize;

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
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is less than 512 bytes.</exception>
    public uint? MaxFrameSize
    {
        get => _maxFrameSize;
        set
        {
            if (value.HasValue)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(value.Value, 512u, nameof(value));
            }

            _maxFrameSize = value;
        }
    }

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

    private sealed class DebugView
    {
        private readonly AmqpOpenPerformative _target;

        public DebugView(AmqpOpenPerformative value)
        {
            _target = value;
        }

        public string ContainerId => _target.ContainerId;
        public string? HostName => _target.HostName;
        public uint? MaxFrameSize => _target.MaxFrameSize;
        public ushort? ChannelMax => _target.ChannelMax;
        public uint? IdleTimeOut => _target.IdleTimeOut;
        public IReadOnlyList<AmqpSymbol>? OutgoingLocales => _target.OutgoingLocales;
        public IReadOnlyList<AmqpSymbol>? IncomingLocales => _target.IncomingLocales;
        public IReadOnlyList<AmqpSymbol>? OfferedCapabilities => _target.OfferedCapabilities;
        public IReadOnlyList<AmqpSymbol>? DesiredCapabilities => _target.DesiredCapabilities;
        public IReadOnlyDictionary<AmqpSymbol, object?>? Properties => _target.Properties;
    }
}
