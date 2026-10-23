using System;
using System.Net;

namespace Assimalign.Cohesion.Connections.Udp;

/// <summary>
/// Defines options for binding a server-side UDP datagram socket.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("UdpBindOptions")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class UdpBindOptions
{
    private int? _receiveBufferSize;
    private int? _sendBufferSize;

    /// <summary>
    /// Gets a new options instance populated with default values.
    /// </summary>
    public static UdpBindOptions Default => new();

    /// <summary>
    /// Gets or sets the local endpoint the UDP socket is bound to.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>127.0.0.1:8082</c>.
    /// </remarks>
    public EndPoint EndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 8082);

    /// <summary>
    /// Gets or sets whether an IPv6 socket also accepts IPv4 traffic (dual mode).
    /// </summary>
    /// <remarks>
    /// Only applied when the bound endpoint uses the IPv6 address family.
    /// Defaults to <see langword="true"/>.
    /// </remarks>
    public bool DualMode { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the local address can be reused while a previous
    /// binding is still lingering.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="false"/>.
    /// </remarks>
    public bool ReuseAddress { get; set; }

    /// <summary>
    /// Gets or sets the socket receive buffer size in bytes, or <see langword="null"/>
    /// to use the operating system default.
    /// </summary>
    /// <remarks>
    /// Accepts zero or a positive byte count, matching the .NET socket buffer setter.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public int? ReceiveBufferSize
    {
        get => _receiveBufferSize;
        set
        {
            if (value is int bufferSize)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(bufferSize, nameof(value));
            }

            _receiveBufferSize = value;
        }
    }

    /// <summary>
    /// Gets or sets the socket send buffer size in bytes, or <see langword="null"/>
    /// to use the operating system default.
    /// </summary>
    /// <remarks>
    /// Accepts zero or a positive byte count, matching the .NET socket buffer setter.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public int? SendBufferSize
    {
        get => _sendBufferSize;
        set
        {
            if (value is int bufferSize)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(bufferSize, nameof(value));
            }

            _sendBufferSize = value;
        }
    }

    private sealed class DebugView
    {
        private readonly UdpBindOptions _target;

        public DebugView(UdpBindOptions value)
        {
            _target = value;
        }

        public EndPoint EndPoint => _target.EndPoint;
        public bool DualMode => _target.DualMode;
        public bool ReuseAddress => _target.ReuseAddress;
        public int? ReceiveBufferSize => _target.ReceiveBufferSize;
        public int? SendBufferSize => _target.SendBufferSize;
    }
}
