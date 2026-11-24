using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Configures how an <see cref="AmqpConnectionListener"/> establishes the AMQP connections it accepts, and
/// what each connection declares in its <c>open</c> (AMQP 1.0 Part 2, section 2.7.1).
/// </summary>
/// <remarks>
/// The listener reads these values once, when
/// <see cref="AmqpConnectionListener.Create(Assimalign.Cohesion.Connections.IDuplexConnectionListener, AmqpConnectionListenerOptions?)"/>
/// creates it; changing the options afterwards does not affect that listener.
/// </remarks>
[DebuggerDisplay("AmqpConnectionListenerOptions {ContainerId,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class AmqpConnectionListenerOptions
{
    private uint _maxFrameSize = 262_144u;
    private TimeSpan _idleTimeout = TimeSpan.FromMinutes(1);
    private TimeSpan _handshakeTimeout = TimeSpan.FromSeconds(10);
    private int _maxConcurrentHandshakes = 512;

    /// <summary>
    /// Initializes new listener options with the defaults.
    /// </summary>
    public AmqpConnectionListenerOptions()
    {
    }

    /// <summary>
    /// Gets or sets the container identifier every accepted connection sends in its <c>open</c>.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="null"/>, which gives the listener a unique identifier of its own when it is
    /// created. A container identifies the endpoint across connections, such as for a peer that resumes
    /// links after reconnecting, so a service that restarts keeps its identifier stable by setting one.
    /// </remarks>
    public string? ContainerId { get; set; }

    /// <summary>
    /// Gets or sets the largest frame, in bytes, an accepted connection takes in. A connection sends frames
    /// no larger than this or the peer's own limit, whichever is smaller.
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
    /// Gets or sets the highest channel number an accepted connection takes, which bounds how many sessions
    /// the peer can begin on it at once. Defaults to 255.
    /// </summary>
    /// <remarks>
    /// A peer that begins a session on a higher channel closes the connection with
    /// <see cref="AmqpErrorCondition.FramingError"/>, as AMQP 1.0 Part 2, section 2.7.1 requires.
    /// </remarks>
    public ushort ChannelMax { get; set; } = 255;

    /// <summary>
    /// Gets or sets how long a peer may stay silent before its connection closes with
    /// <see cref="AmqpErrorCondition.ResourceLimitExceeded"/>; <see cref="TimeSpan.Zero"/> disables the limit.
    /// </summary>
    /// <remarks>
    /// Defaults to one minute. A connection declares half the value in its <c>open</c>, as AMQP 1.0 Part 2,
    /// section 2.4.5 recommends, so a peer that sends a frame at least that often never trips it. Whatever
    /// this value, a connection keeps the peer's own idle timeout by sending empty frames, down to 100
    /// milliseconds; a peer that asks for less is refused with <see cref="AmqpErrorCondition.NotImplemented"/>.
    /// A value other than zero is therefore at least 200 milliseconds, so the half a connection declares is
    /// one this library keeps.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is negative, above zero but under 200 milliseconds, or longer than <see cref="uint.MaxValue"/> - 1 milliseconds.</exception>
    public TimeSpan IdleTimeout
    {
        get => _idleTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, AmqpConnectionSettings.MaxTimeout);

            if (value > TimeSpan.Zero && value < AmqpConnectionSettings.MinIdleTimeout)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.IdleTimeoutTooShort, AmqpConnectionSettings.MinIdleTimeout));
            }

            _idleTimeout = value;
        }
    }

    /// <summary>
    /// Gets or sets how long the listener allows for establishing one connection: accepting the AMQP stream
    /// of a multiplexed carrier, exchanging the protocol headers, the SASL exchange and the <c>open</c> frames.
    /// </summary>
    /// <remarks>
    /// Defaults to 10 seconds. A non-positive value disables the limit. The limit applies to each
    /// connection on its own: one that exceeds it is disposed and the listener keeps accepting. With the
    /// limit disabled, a peer that never sends its protocol header holds one of the listener's
    /// <see cref="MaxConcurrentHandshakes"/> until it disconnects. A connection that sent <c>close</c>
    /// waits the same time for the peer's, or 30 seconds when the limit is disabled.
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

    /// <summary>
    /// Gets or sets the TLS layer that secures a connection whose peer asks for TLS with the AMQP TLS protocol
    /// header, before SASL and AMQP (AMQP 1.0 Part 5, section 5.2), or <see langword="null"/> to decline TLS.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to <see langword="null"/>: a peer that sends the TLS protocol header is answered with the header the
    /// listener accepts, SASL or AMQP, and refused, as AMQP 1.0 Part 2, section 2.2 requires. A carrier listener
    /// composed with a TLS layer secures every connection before AMQP starts (Part 5, section 5.2.1) and needs no
    /// layer here.
    /// </para>
    /// <para>
    /// With a layer, the listener answers the TLS header with its own, hands the carrier connection to
    /// <see cref="IConnectionLayer.UpgradeAsync(IDuplexConnection, System.Threading.CancellationToken)"/>, which runs
    /// the TLS handshake, and reads the peer's next protocol header over the connection the layer returns; the SASL
    /// exchange and the AMQP connection run over it too, so the connection's features carry the layer's, such as its
    /// TLS facts. The layer is typically a <c>TlsConnectionLayer</c> built with server options, from
    /// <c>Assimalign.Cohesion.Connections.Security</c>. TLS is negotiated once: a second TLS header is refused. The
    /// handshake counts against <see cref="HandshakeTimeout"/>, and one that fails ends only its connection. A peer
    /// may still skip TLS and start with SASL or AMQP, unless <see cref="RequireTls"/> is set.
    /// </para>
    /// </remarks>
    public IConnectionLayer? TlsLayer { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether every accepted connection must be secured with TLS, by its carrier or
    /// by <see cref="TlsLayer"/> after the AMQP TLS protocol header (AMQP 1.0 Part 5, section 5.2).
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="false"/>. When <see langword="true"/>, a peer whose carrier connection is not
    /// secured and that starts with any other header than the TLS protocol header is answered with the TLS header and
    /// refused. A carrier connection whose capabilities report <see cref="ConnectionSecurity.Tls"/>, such as one a
    /// TLS-composed carrier listener accepted, meets the requirement without a TLS header. Over a carrier listener
    /// that does not secure its connections, the listener's <see cref="AmqpConnectionListener.Capabilities"/> are then
    /// those <see cref="TlsLayer"/> describes, and <c>AmqpConnectionListener.Create</c> refuses options that set no
    /// <see cref="TlsLayer"/>, since no client could meet the requirement.
    /// </remarks>
    public bool RequireTls { get; set; }

    /// <summary>
    /// Gets the SASL mechanisms the listener offers, in order of preference (AMQP 1.0 Part 5, section 5.3).
    /// </summary>
    /// <remarks>
    /// Empty by default, which accepts connections without authentication and refuses a peer that asks for
    /// SASL. With at least one mechanism, every peer must authenticate with one of them: a peer that starts
    /// with the AMQP protocol header instead is refused. The identity a mechanism authenticated is on the
    /// connection's <see cref="IAmqpSaslConnectionInfo"/> feature. Each mechanism's name must be unique.
    /// </remarks>
    public IList<IAmqpSaslServerMechanism> SaslMechanisms { get; } = new List<IAmqpSaslServerMechanism>();

    /// <summary>
    /// Gets or sets the options of the sessions peers begin on accepted connections, and of sessions the
    /// application begins without options; <see langword="null"/> uses the defaults.
    /// </summary>
    public AmqpSessionOptions? SessionOptions { get; set; }

    /// <summary>
    /// Gets or sets the extension capabilities an accepted connection offers in its <c>open</c>, or <see langword="null"/>.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? OfferedCapabilities { get; set; }

    /// <summary>
    /// Gets or sets the extension capabilities an accepted connection desires in its <c>open</c>, or <see langword="null"/>.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? DesiredCapabilities { get; set; }

    /// <summary>
    /// Gets or sets the connection properties an accepted connection sends in its <c>open</c>, such as the
    /// product name and version, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The whole <c>open</c> must fit in 512 bytes, the most a peer accepts before the connection opens;
    /// establishment fails otherwise.
    /// </remarks>
    public IReadOnlyDictionary<AmqpSymbol, object?>? Properties { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a peer's <c>attach</c> of a link that is attached on another of the
    /// listener's connections, or on another session, takes the link over (AMQP 1.0 Part 2, section 2.6.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to <see langword="false"/>: each session keeps its own link names, refuses a second <c>attach</c>
    /// of a name and direction it holds with <c>amqp:not-allowed</c>, and does not see links on other sessions or
    /// connections.
    /// </para>
    /// <para>
    /// When <see langword="true"/>, the listener tracks every link its connections hold by the peer's container
    /// identifier, its own, the link's name and its direction, which together identify a link. A peer's
    /// <c>attach</c> of a link held elsewhere is handed to the application as a new link request, and the earlier
    /// attachment is closed with <c>amqp:link:stolen</c>, its pending sends and receives failing with a
    /// <see cref="AmqpErrorCode.LinkDetached"/> error that carries the condition. This lets a client whose
    /// connection died attach its links again from a new connection while the old one still waits for its idle
    /// timeout. A link that is detaching, or was suspended, is no longer held, so attaching it again resumes it
    /// rather than stealing it. The application's own attach of a link held elsewhere throws
    /// <see cref="System.InvalidOperationException"/> before anything is sent, as a duplicate on its own session
    /// does: the application holds that link, and closes it first.
    /// </para>
    /// </remarks>
    public bool AllowLinkStealing { get; set; }

    private sealed class DebugView
    {
        private readonly AmqpConnectionListenerOptions _options;

        public DebugView(AmqpConnectionListenerOptions options)
        {
            _options = options;
        }

        public string? ContainerId => _options.ContainerId;
        public uint MaxFrameSize => _options.MaxFrameSize;
        public ushort ChannelMax => _options.ChannelMax;
        public TimeSpan IdleTimeout => _options.IdleTimeout;
        public TimeSpan HandshakeTimeout => _options.HandshakeTimeout;
        public int MaxConcurrentHandshakes => _options.MaxConcurrentHandshakes;
        public IConnectionLayer? TlsLayer => _options.TlsLayer;
        public bool RequireTls => _options.RequireTls;
        public IList<IAmqpSaslServerMechanism> SaslMechanisms => _options.SaslMechanisms;
        public AmqpSessionOptions? SessionOptions => _options.SessionOptions;
        public IReadOnlyList<AmqpSymbol>? OfferedCapabilities => _options.OfferedCapabilities;
        public IReadOnlyList<AmqpSymbol>? DesiredCapabilities => _options.DesiredCapabilities;
        public IReadOnlyDictionary<AmqpSymbol, object?>? Properties => _options.Properties;
        public bool AllowLinkStealing => _options.AllowLinkStealing;
    }
}
