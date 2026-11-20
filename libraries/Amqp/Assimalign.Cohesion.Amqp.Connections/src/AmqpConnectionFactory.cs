using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Opens AMQP connections over the carrier connections a lower-level factory connects.
/// </summary>
/// <remarks>
/// <para>
/// The carrier is consumed by capability: its factory must produce reliable, ordered byte streams
/// (<see cref="ConnectionDelivery.Stream"/> with <see cref="ConnectionCapabilities.IsReliable"/> and
/// <see cref="ConnectionCapabilities.IsOrdered"/>). Over a single-stream carrier each connected
/// <see cref="IDuplexConnection"/> carries one AMQP connection. Over a multiplexed carrier each connected
/// <see cref="IMultiplexedConnection"/> carries one AMQP connection, on a bidirectional stream the factory
/// opens. TLS is the carrier's concern: compose the carrier factory with a TLS layer first.
/// </para>
/// <para>
/// <see cref="ConnectionFactory{TConnection}.ConnectAsync(EndPoint, CancellationToken)"/> connects a new
/// carrier on every call and returns an open connection: the protocol headers, the SASL exchange when
/// <see cref="AmqpConnectionFactoryOptions.SaslMechanism"/> configures one, and both <c>open</c> frames are
/// done, within <see cref="AmqpConnectionFactoryOptions.HandshakeTimeout"/>. When establishment fails or
/// times out, the factory disposes the carrier before the exception reaches the caller. The caller owns
/// every connection it receives; the factory keeps no reference to it and does not own the carrier
/// factory.
/// </para>
/// </remarks>
[DebuggerDisplay("AmqpConnectionFactory {ContainerId,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class AmqpConnectionFactory : ConnectionFactory<IAmqpConnection>, IAmqpConnectionFactory
{
    private readonly IDuplexConnectionFactory _factory;
    private readonly AmqpConnectionSettings _settings;

    private AmqpConnectionFactory(IDuplexConnectionFactory factory, AmqpConnectionSettings settings)
    {
        _factory = factory;
        _settings = settings;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The carrier factory's capabilities; over a multiplexed carrier, those of one of its streams, so
    /// <see cref="ConnectionCapabilities.IsMultiplexed"/> is <see langword="false"/>.
    /// </remarks>
    public override ConnectionCapabilities Capabilities => _factory.Capabilities;

    /// <inheritdoc />
    public string ContainerId => _settings.ContainerId;

    /// <summary>
    /// Creates an AMQP connection factory over a single-stream carrier factory.
    /// </summary>
    /// <param name="factory">The carrier factory.</param>
    /// <param name="options">The establishment options, read once here; <see langword="null"/> uses the defaults.</param>
    /// <returns>The AMQP connection factory.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the carrier does not deliver a reliable, ordered byte stream.</exception>
    public static AmqpConnectionFactory Create(IDuplexConnectionFactory factory, AmqpConnectionFactoryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        factory.Capabilities.ThrowIfNotAmqpCarrier(nameof(factory));

        return Compose(factory, options);
    }

    /// <summary>
    /// Creates an AMQP connection factory over a multiplexed carrier factory.
    /// </summary>
    /// <param name="factory">The multiplexed carrier factory.</param>
    /// <param name="options">The establishment options, read once here; <see langword="null"/> uses the defaults.</param>
    /// <returns>The AMQP connection factory.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the carrier does not deliver a reliable, ordered byte stream.</exception>
    public static AmqpConnectionFactory Create(IMultiplexedConnectionFactory factory, AmqpConnectionFactoryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        factory.Capabilities.ThrowIfNotAmqpCarrier(nameof(factory));

        return Compose(new AmqpMultiplexedCarrierFactory(factory), options);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="endPoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="AmqpException">
    /// Thrown with <see cref="AmqpErrorCode.ProtocolViolation"/> when the peer's protocol header, SASL frames
    /// or <c>open</c> break the protocol; with <see cref="AmqpErrorCode.AuthenticationFailed"/> when SASL
    /// authentication fails or one side requires it and the other does not use it; with
    /// <see cref="AmqpErrorCode.ConnectionClosed"/> when the peer closes the carrier during establishment.
    /// </exception>
    /// <exception cref="TimeoutException">Thrown when establishment exceeds <see cref="AmqpConnectionFactoryOptions.HandshakeTimeout"/>.</exception>
    protected override async ValueTask<IAmqpConnection> ConnectCoreAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endPoint);

        IDuplexConnection carrier = await _factory.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
        AmqpConnectionEndpoint? connection = null;

        try
        {
            // An unnamed host defaults to the name the caller connected to, which an IP address does not carry.
            string? hostName = _settings.HostName ?? (endPoint as DnsEndPoint)?.Host;

            connection = await AmqpHandshake.EstablishAsync(carrier, _settings, connecting: true, hostName, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        finally
        {
            if (connection is null)
            {
                await carrier.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static AmqpConnectionFactory Compose(IDuplexConnectionFactory carrier, AmqpConnectionFactoryOptions? options)
    {
        options ??= new AmqpConnectionFactoryOptions();

        AmqpConnectionSettings settings = new()
        {
            ContainerId = options.ContainerId ?? AmqpConnectionSettings.CreateContainerId(),
            HostName = options.HostName,
            MaxFrameSize = options.MaxFrameSize,
            ChannelMax = options.ChannelMax,
            IdleTimeout = options.IdleTimeout,
            HandshakeTimeout = options.HandshakeTimeout,
            ClientMechanism = options.SaslMechanism,
            SessionSettings = AmqpSessionSettings.From(options.SessionOptions),
            OfferedCapabilities = options.OfferedCapabilities,
            DesiredCapabilities = options.DesiredCapabilities,
            Properties = options.Properties
        };

        return new AmqpConnectionFactory(carrier, settings);
    }

    private sealed class DebugView
    {
        private readonly AmqpConnectionFactory _factory;

        public DebugView(AmqpConnectionFactory factory)
        {
            _factory = factory;
        }

        public ConnectionCapabilities Capabilities => _factory.Capabilities;
        public string ContainerId => _factory.ContainerId;
    }
}
