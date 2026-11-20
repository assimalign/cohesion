using System;

using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Configures how an <see cref="AmqpConnectionListener"/> establishes the AMQP connections it accepts.
/// </summary>
/// <remarks>
/// The listener reads these values once, when <see cref="AmqpConnectionListener.Create(Assimalign.Cohesion.Connections.IDuplexConnectionListener, AmqpConnectionListenerOptions?)"/>
/// creates it; changing the options afterwards does not affect that listener.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("AmqpConnectionListenerOptions")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class AmqpConnectionListenerOptions
{
    private uint _maxFrameSize = 262_144u;
    private TimeSpan _handshakeTimeout = TimeSpan.FromSeconds(10);
    private int _maxConcurrentHandshakes = 512;

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
    /// Gets or sets a value indicating whether the listener exchanges the protocol headers while it
    /// establishes each connection, before <c>AcceptAsync</c> returns it.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="true"/>. When <see langword="false"/>, an accepted connection has
    /// exchanged no bytes, and its owner calls <see cref="IAmqpConnection.NegotiateAsync(System.Threading.CancellationToken)"/>
    /// (and applies its own time limit) before exchanging frames.
    /// </remarks>
    public bool AutoNegotiateProtocolHeader { get; set; } = true;

    /// <summary>
    /// Gets or sets the largest AMQP frame, in bytes, that an accepted connection sends.
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
    /// Gets or sets how long the listener allows for establishing one connection: acquiring the AMQP
    /// stream of a multiplexed carrier and exchanging the protocol headers.
    /// </summary>
    /// <remarks>
    /// Defaults to 10 seconds. A non-positive value disables the limit. The limit applies to each
    /// connection on its own: one that exceeds it is disposed and the listener keeps accepting. With the
    /// limit disabled, a peer that never sends its protocol header holds one of the listener's
    /// <see cref="MaxConcurrentHandshakes"/> until it disconnects.
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

    /// <summary>
    /// Gets or sets the most connections the listener holds at once while it establishes them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to 512. Each accepted carrier connection is established on its own task, so a slow or
    /// silent peer delays only itself. A connection counts against this limit from the moment the
    /// listener accepts its carrier until <c>AcceptAsync</c> returns it, or its establishment fails or
    /// exceeds <see cref="HandshakeTimeout"/>. At the limit the listener stops accepting from the carrier
    /// listener, and further peers wait in the carrier's own backlog (for TCP, the operating system's
    /// listen queue).
    /// </para>
    /// <para>
    /// The limit trades memory for availability: while it is reached, a new peer waits until a slot
    /// frees, which a stalled peer does at the latest after <see cref="HandshakeTimeout"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is less than 1.</exception>
    public int MaxConcurrentHandshakes
    {
        get => _maxConcurrentHandshakes;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);

            _maxConcurrentHandshakes = value;
        }
    }

    private sealed class DebugView
    {
        private readonly AmqpConnectionListenerOptions _target;

        public DebugView(AmqpConnectionListenerOptions value)
        {
            _target = value;
        }

        public AmqpProtocolHeader InitialProtocolHeader => _target.InitialProtocolHeader;
        public bool AutoNegotiateProtocolHeader => _target.AutoNegotiateProtocolHeader;
        public uint MaxFrameSize => _target.MaxFrameSize;
        public TimeSpan HandshakeTimeout => _target.HandshakeTimeout;
        public int MaxConcurrentHandshakes => _target.MaxConcurrentHandshakes;
    }
}
