using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp.Connections.Internal;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Establishes AMQP connections over the carrier connections a lower-level factory connects.
/// </summary>
/// <remarks>
/// <para>
/// The carrier is consumed by capability: its factory must produce reliable, ordered byte streams
/// (<see cref="ConnectionDelivery.Stream"/> with <see cref="ConnectionCapabilities.IsReliable"/> and
/// <see cref="ConnectionCapabilities.IsOrdered"/>). Over a single-stream carrier each connected
/// <see cref="IDuplexConnection"/> carries one AMQP connection. Over a multiplexed carrier each connected
/// <see cref="IMultiplexedConnection"/> carries one AMQP connection, on a bidirectional stream the factory
/// opens.
/// </para>
/// <para>
/// <see cref="ConnectionFactory{TConnection}.ConnectAsync(EndPoint, CancellationToken)"/> connects a new
/// carrier on every call and returns an established connection: a multiplexed carrier's AMQP stream has
/// been opened and, with <see cref="AmqpConnectionFactoryOptions.AutoNegotiateProtocolHeader"/>, the
/// protocol headers have been exchanged, within <see cref="AmqpConnectionFactoryOptions.HandshakeTimeout"/>.
/// When establishment fails or times out, the factory disposes the carrier before the exception reaches
/// the caller. The caller owns every connection it receives; the factory keeps no reference to it and
/// does not own the carrier factory.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("AmqpConnectionFactory")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class AmqpConnectionFactory : ConnectionFactory<IAmqpConnection>, IAmqpConnectionFactory
{
    private readonly IDuplexConnectionFactory _factory;

    private AmqpConnectionFactory(IDuplexConnectionFactory factory)
    {
        _factory = factory;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The carrier factory's capabilities; over a multiplexed carrier, those of one of its streams, so
    /// <see cref="ConnectionCapabilities.IsMultiplexed"/> is <see langword="false"/>.
    /// </remarks>
    public override ConnectionCapabilities Capabilities => _factory.Capabilities;

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
    /// <exception cref="AmqpProtocolException">Thrown when the peer's protocol header is truncated or invalid.</exception>
    /// <exception cref="TimeoutException">Thrown when establishment exceeds <see cref="AmqpConnectionFactoryOptions.HandshakeTimeout"/>.</exception>
    protected override async ValueTask<IAmqpConnection> ConnectCoreAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endPoint);

        // The establishment layer produced every connection the layered factory returns.
        return (IAmqpConnection)await _factory.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
    }

    private static AmqpConnectionFactory Compose(IDuplexConnectionFactory carrier, AmqpConnectionFactoryOptions? options)
    {
        options ??= new AmqpConnectionFactoryOptions();

        AmqpConnectionSettings settings = new(
            options.InitialProtocolHeader,
            options.AutoNegotiateProtocolHeader,
            options.MaxFrameSize,
            options.HandshakeTimeout);

        return new AmqpConnectionFactory(carrier.Use(new AmqpConnectionLayer(settings)));
    }

    private sealed class DebugView
    {
        private readonly AmqpConnectionFactory _target;

        public DebugView(AmqpConnectionFactory value)
        {
            _target = value;
        }

        public ConnectionCapabilities Capabilities => _target.Capabilities;
    }
}
