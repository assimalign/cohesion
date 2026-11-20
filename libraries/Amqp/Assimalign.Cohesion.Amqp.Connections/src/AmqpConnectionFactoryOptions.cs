using System;
using System.Collections.Generic;
using System.Diagnostics;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Configures how an <see cref="AmqpConnectionFactory"/> establishes the AMQP connections it opens, and
/// what each connection declares in its <c>open</c> (AMQP 1.0 Part 2, section 2.7.1).
/// </summary>
/// <remarks>
/// The factory reads these values once, when
/// <see cref="AmqpConnectionFactory.Create(Assimalign.Cohesion.Connections.IDuplexConnectionFactory, AmqpConnectionFactoryOptions?)"/>
/// creates it; changing the options afterwards does not affect that factory.
/// </remarks>
[DebuggerDisplay("AmqpConnectionFactoryOptions {ContainerId,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class AmqpConnectionFactoryOptions
{
    private uint _maxFrameSize = 262_144u;
    private TimeSpan _idleTimeout = TimeSpan.FromMinutes(1);
    private TimeSpan _handshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Initializes new factory options with the defaults.
    /// </summary>
    public AmqpConnectionFactoryOptions()
    {
    }

    /// <summary>
    /// Gets or sets the container identifier every connection the factory opens sends in its <c>open</c>.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="null"/>, which gives the factory a unique identifier of its own when it is
    /// created. A container identifies the endpoint across connections, such as for a broker that resumes a
    /// durable subscription after the client reconnects, so a client that restarts keeps its identifier
    /// stable by setting one.
    /// </remarks>
    public string? ContainerId { get; set; }

    /// <summary>
    /// Gets or sets the host name a connection names in its <c>open</c> and SASL <c>init</c>, which tells a
    /// peer that serves several virtual hosts which one the connection is for.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="null"/>, which names the host of a <see cref="System.Net.DnsEndPoint"/>
    /// and no host for other endpoints. Set it when connecting to an IP address of a service that expects
    /// its own name.
    /// </remarks>
    public string? HostName { get; set; }

    /// <summary>
    /// Gets or sets the largest frame, in bytes, a connection takes in. A connection sends frames no larger
    /// than this or the peer's own limit, whichever is smaller.
    /// </summary>
    /// <remarks>Defaults to 262,144 bytes.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is less than 512 bytes, the AMQP 1.0 minimum.</exception>
    public uint MaxFrameSize
    {
        get => _maxFrameSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, AmqpFrame.MinMaxFrameSize);

            _maxFrameSize = value;
        }
    }

    /// <summary>
    /// Gets or sets the highest channel number a connection takes, which bounds how many sessions both peers
    /// can begin on it at once. Defaults to 255.
    /// </summary>
    /// <remarks>
    /// A connection begins its own sessions on channels up to this value or the peer's limit, whichever is
    /// smaller. A peer that begins a session on a higher channel closes the connection with
    /// <see cref="AmqpErrorCondition.FramingError"/>, as AMQP 1.0 Part 2, section 2.7.1 requires.
    /// </remarks>
    public ushort ChannelMax { get; set; } = 255;

    /// <summary>
    /// Gets or sets how long the peer may stay silent before the connection closes with
    /// <see cref="AmqpErrorCondition.ResourceLimitExceeded"/>; <see cref="TimeSpan.Zero"/> disables the limit.
    /// </summary>
    /// <remarks>
    /// Defaults to one minute. A connection declares half the value in its <c>open</c>, as AMQP 1.0 Part 2,
    /// section 2.4.5 recommends, so a peer that sends a frame at least that often never trips it. Whatever
    /// this value, a connection keeps the peer's own idle timeout by sending empty frames.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is negative or longer than <see cref="uint.MaxValue"/> - 1 milliseconds.</exception>
    public TimeSpan IdleTimeout
    {
        get => _idleTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, AmqpConnectionSettings.MaxTimeout);

            _idleTimeout = value;
        }
    }

    /// <summary>
    /// Gets or sets how long the factory allows for establishing a connection once its carrier is connected:
    /// opening the AMQP stream of a multiplexed carrier, exchanging the protocol headers, the SASL exchange
    /// and the <c>open</c> frames.
    /// </summary>
    /// <remarks>
    /// Defaults to 10 seconds. A non-positive value disables the limit. Connecting the carrier itself is
    /// bounded by the carrier factory's own options. A connection that sent <c>close</c> waits the same time
    /// for the peer's, or 30 seconds when the limit is disabled.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is longer than <see cref="uint.MaxValue"/> - 1 milliseconds.</exception>
    public TimeSpan HandshakeTimeout
    {
        get => _handshakeTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, AmqpConnectionSettings.MaxTimeout);

            _handshakeTimeout = value;
        }
    }

    /// <summary>
    /// Gets or sets the SASL mechanism connections authenticate with (AMQP 1.0 Part 5, section 5.3), or
    /// <see langword="null"/> to connect without authentication.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="null"/>. With a mechanism, establishment fails with
    /// <see cref="AmqpErrorCode.AuthenticationFailed"/> when the peer does not offer it or refuses the
    /// credentials; without one, it fails the same way when the peer requires authentication.
    /// </remarks>
    public IAmqpSaslClientMechanism? SaslMechanism { get; set; }

    /// <summary>
    /// Gets or sets the options of sessions the application begins without options, and of sessions the peer
    /// begins; <see langword="null"/> uses the defaults.
    /// </summary>
    public AmqpSessionOptions? SessionOptions { get; set; }

    /// <summary>
    /// Gets or sets the extension capabilities a connection offers in its <c>open</c>, or <see langword="null"/>.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? OfferedCapabilities { get; set; }

    /// <summary>
    /// Gets or sets the extension capabilities a connection desires in its <c>open</c>, or <see langword="null"/>.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? DesiredCapabilities { get; set; }

    /// <summary>
    /// Gets or sets the connection properties a connection sends in its <c>open</c>, such as the product name
    /// and version, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The whole <c>open</c> must fit in 512 bytes, the most a peer accepts before the connection opens;
    /// establishment fails otherwise.
    /// </remarks>
    public IReadOnlyDictionary<AmqpSymbol, object?>? Properties { get; set; }

    private sealed class DebugView
    {
        private readonly AmqpConnectionFactoryOptions _options;

        public DebugView(AmqpConnectionFactoryOptions options)
        {
            _options = options;
        }

        public string? ContainerId => _options.ContainerId;
        public string? HostName => _options.HostName;
        public uint MaxFrameSize => _options.MaxFrameSize;
        public ushort ChannelMax => _options.ChannelMax;
        public TimeSpan IdleTimeout => _options.IdleTimeout;
        public TimeSpan HandshakeTimeout => _options.HandshakeTimeout;
        public IAmqpSaslClientMechanism? SaslMechanism => _options.SaslMechanism;
        public AmqpSessionOptions? SessionOptions => _options.SessionOptions;
        public IReadOnlyList<AmqpSymbol>? OfferedCapabilities => _options.OfferedCapabilities;
        public IReadOnlyList<AmqpSymbol>? DesiredCapabilities => _options.DesiredCapabilities;
        public IReadOnlyDictionary<AmqpSymbol, object?>? Properties => _options.Properties;
    }
}
