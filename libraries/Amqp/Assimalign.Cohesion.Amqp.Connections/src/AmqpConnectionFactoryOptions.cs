using System;

using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Configures how an <see cref="AmqpConnectionFactory"/> establishes the AMQP connections it connects.
/// </summary>
/// <remarks>
/// The factory reads these values once, when <see cref="AmqpConnectionFactory.Create(Assimalign.Cohesion.Connections.IDuplexConnectionFactory, AmqpConnectionFactoryOptions?)"/>
/// creates it; changing the options afterwards does not affect that factory.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("AmqpConnectionFactoryOptions")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class AmqpConnectionFactoryOptions
{
    private uint _maxFrameSize = 262_144u;
    private TimeSpan _handshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the AMQP protocol header each connection sends for its first protocol phase.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="AmqpProtocolHeader.Amqp10"/>. Use <see cref="AmqpProtocolHeader.Sasl10"/> to
    /// start every connection in the SASL layer, then call
    /// <see cref="IAmqpConnection.SwitchProtocolAsync(AmqpProtocolHeader, System.Threading.CancellationToken)"/>
    /// once authentication completes.
    /// </remarks>
    public AmqpProtocolHeader InitialProtocolHeader { get; set; } = AmqpProtocolHeader.Amqp10;

    /// <summary>
    /// Gets or sets a value indicating whether the factory exchanges the protocol headers while it
    /// establishes each connection, before <c>ConnectAsync</c> returns it.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="true"/>. When <see langword="false"/>, a connected connection has
    /// exchanged no bytes, and its owner calls <see cref="IAmqpConnection.NegotiateAsync(System.Threading.CancellationToken)"/>
    /// before exchanging frames.
    /// </remarks>
    public bool AutoNegotiateProtocolHeader { get; set; } = true;

    /// <summary>
    /// Gets or sets the largest AMQP frame, in bytes, that a connected connection sends.
    /// </summary>
    /// <remarks>Defaults to 262,144 bytes.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is less than 512 bytes, the AMQP 1.0 minimum.</exception>
    public uint MaxFrameSize
    {
        get => _maxFrameSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 512u);

            _maxFrameSize = value;
        }
    }

    /// <summary>
    /// Gets or sets how long the factory allows for establishing one connection once its carrier is
    /// connected: opening the AMQP stream of a multiplexed carrier and exchanging the protocol headers.
    /// </summary>
    /// <remarks>
    /// Defaults to 10 seconds. A non-positive value disables the limit. A connection that exceeds it is
    /// disposed and <c>ConnectAsync</c> throws <see cref="TimeoutException"/>. Connecting the carrier itself
    /// is the carrier factory's to limit, and the caller's cancellation token covers both.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is longer than <see cref="uint.MaxValue"/> - 1 milliseconds.</exception>
    public TimeSpan HandshakeTimeout
    {
        get => _handshakeTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, AmqpConnectionSettings.MaxHandshakeTimeout);

            _handshakeTimeout = value;
        }
    }

    private sealed class DebugView
    {
        private readonly AmqpConnectionFactoryOptions _target;

        public DebugView(AmqpConnectionFactoryOptions value)
        {
            _target = value;
        }

        public AmqpProtocolHeader InitialProtocolHeader => _target.InitialProtocolHeader;
        public bool AutoNegotiateProtocolHeader => _target.AutoNegotiateProtocolHeader;
        public uint MaxFrameSize => _target.MaxFrameSize;
        public TimeSpan HandshakeTimeout => _target.HandshakeTimeout;
    }
}
