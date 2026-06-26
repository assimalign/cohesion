using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp.Connections.Internal;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Establishes AMQP connections over carrier connections produced by a lower-level connection factory.
/// </summary>
/// <remarks>
/// <para>
/// The client transport consumes the carrier by capability: the factory must produce reliable,
/// ordered byte streams (<see cref="ConnectionDelivery.Stream"/> with
/// <see cref="ConnectionCapabilities.IsReliable"/> and <see cref="ConnectionCapabilities.IsOrdered"/>).
/// </para>
/// <para>
/// For a single-stream carrier each established <see cref="IConnection"/> backs one AMQP connection.
/// For a multiplexed carrier each established <see cref="IMultiplexedConnection"/> backs one AMQP
/// connection whose bidirectional carrier stream is opened when the connection context is opened.
/// </para>
/// </remarks>
public sealed class AmqpClientTransport : IAsyncDisposable
{
    private readonly IConnectionFactory? _factory;
    private readonly IMultiplexedConnectionFactory? _multiplexedFactory;
    private readonly EndPoint _endPoint;
    private readonly AmqpTransportOptions _options;
    private readonly List<AmqpConnection> _connections;
    private bool _isDisposed;

    /// <summary>
    /// Initializes a new AMQP client transport over a single-stream carrier factory.
    /// </summary>
    /// <param name="factory">The carrier connection factory.</param>
    /// <param name="endPoint">The remote endpoint to connect to.</param>
    /// <param name="options">The AMQP transport options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> or <paramref name="endPoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the carrier does not deliver a reliable, ordered byte stream.</exception>
    public AmqpClientTransport(IConnectionFactory factory, EndPoint endPoint, AmqpTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(endPoint);

        factory.Capabilities.ThrowIfNotAmqpCarrier(nameof(factory));

        _factory = factory;
        _endPoint = endPoint;
        _options = options ?? new AmqpTransportOptions();
        _connections = new List<AmqpConnection>();
    }

    /// <summary>
    /// Initializes a new AMQP client transport over a multiplexed carrier factory.
    /// </summary>
    /// <param name="factory">The multiplexed carrier connection factory.</param>
    /// <param name="endPoint">The remote endpoint to connect to.</param>
    /// <param name="options">The AMQP transport options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> or <paramref name="endPoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the carrier does not deliver a reliable, ordered byte stream.</exception>
    public AmqpClientTransport(IMultiplexedConnectionFactory factory, EndPoint endPoint, AmqpTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(endPoint);

        factory.Capabilities.ThrowIfNotAmqpCarrier(nameof(factory));

        _multiplexedFactory = factory;
        _endPoint = endPoint;
        _options = options ?? new AmqpTransportOptions();
        _connections = new List<AmqpConnection>();
    }

    /// <summary>
    /// Gets the remote endpoint the transport connects to.
    /// </summary>
    public EndPoint EndPoint => _endPoint;

    /// <summary>
    /// Gets the active AMQP connections opened by this transport.
    /// </summary>
    public IReadOnlyCollection<AmqpConnection> Connections => _connections.AsReadOnly();

    /// <summary>
    /// Establishes a new AMQP connection to the configured remote endpoint.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the connect operation.</param>
    /// <returns>The established AMQP connection.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the transport has been disposed.</exception>
    public async ValueTask<AmqpConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        AmqpTransportConnection connection;

        if (_factory is not null)
        {
            IConnection carrier = await _factory.ConnectAsync(_endPoint, cancellationToken).ConfigureAwait(false);

            connection = new AmqpSingleStreamTransportConnection(carrier, _options);
        }
        else
        {
            IMultiplexedConnection carrier = await _multiplexedFactory!.ConnectAsync(_endPoint, cancellationToken).ConfigureAwait(false);

            connection = new AmqpMultiplexedTransportConnection(carrier, opensCarrierStream: true, _options);
        }

        connection.OnDispose = () => _connections.Remove(connection);

        _connections.Add(connection);

        return connection;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        foreach (AmqpConnection connection in _connections.ToArray())
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _connections.Clear();
    }
}
