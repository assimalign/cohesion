using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp.Transports.Internal;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Accepts AMQP connections over carrier connections produced by a lower-level connection listener.
/// </summary>
/// <remarks>
/// <para>
/// The server transport consumes the carrier by capability: the listener must produce reliable,
/// ordered byte streams (<see cref="ConnectionDelivery.Stream"/> with
/// <see cref="ConnectionCapabilities.IsReliable"/> and <see cref="ConnectionCapabilities.IsOrdered"/>).
/// </para>
/// <para>
/// For a single-stream carrier each accepted <see cref="IConnection"/> backs one AMQP connection.
/// For a multiplexed carrier each accepted <see cref="IMultiplexedConnection"/> backs one AMQP
/// connection whose carrier stream is accepted from the remote peer when the connection context
/// is opened. The transport takes ownership of the listener and disposes it with the transport.
/// </para>
/// </remarks>
public sealed class AmqpServerTransport : IAsyncDisposable
{
    private readonly IConnectionListener? _listener;
    private readonly IMultiplexedConnectionListener? _multiplexedListener;
    private readonly AmqpTransportOptions _options;
    private readonly List<AmqpConnection> _connections;
    private bool _isDisposed;

    /// <summary>
    /// Initializes a new AMQP server transport over a single-stream carrier listener.
    /// </summary>
    /// <param name="listener">The carrier connection listener.</param>
    /// <param name="options">The AMQP transport options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="listener"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the carrier does not deliver a reliable, ordered byte stream.</exception>
    public AmqpServerTransport(IConnectionListener listener, AmqpTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(listener);

        listener.Capabilities.ThrowIfNotAmqpCarrier(nameof(listener));

        _listener = listener;
        _options = options ?? new AmqpTransportOptions();
        _connections = new List<AmqpConnection>();
    }

    /// <summary>
    /// Initializes a new AMQP server transport over a multiplexed carrier listener.
    /// </summary>
    /// <param name="listener">The multiplexed carrier connection listener.</param>
    /// <param name="options">The AMQP transport options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="listener"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the carrier does not deliver a reliable, ordered byte stream.</exception>
    public AmqpServerTransport(IMultiplexedConnectionListener listener, AmqpTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(listener);

        listener.Capabilities.ThrowIfNotAmqpCarrier(nameof(listener));

        _multiplexedListener = listener;
        _options = options ?? new AmqpTransportOptions();
        _connections = new List<AmqpConnection>();
    }

    /// <summary>
    /// Gets the active AMQP connections accepted by this transport.
    /// </summary>
    public IReadOnlyCollection<AmqpConnection> Connections => _connections.AsReadOnly();

    /// <summary>
    /// Accepts the next inbound AMQP connection.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the accept operation.</param>
    /// <returns>The accepted AMQP connection.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the transport has been disposed.</exception>
    public async ValueTask<AmqpConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        AmqpTransportConnection connection;

        if (_listener is not null)
        {
            IConnection carrier = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);

            connection = new AmqpSingleStreamTransportConnection(carrier, _options);
        }
        else
        {
            IMultiplexedConnection carrier = await _multiplexedListener!.AcceptAsync(cancellationToken).ConfigureAwait(false);

            connection = new AmqpMultiplexedTransportConnection(carrier, opensCarrierStream: false, _options);
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

        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
        }

        if (_multiplexedListener is not null)
        {
            await _multiplexedListener.DisposeAsync().ConfigureAwait(false);
        }
    }
}
