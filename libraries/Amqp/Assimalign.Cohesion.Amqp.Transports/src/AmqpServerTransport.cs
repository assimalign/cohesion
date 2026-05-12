using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp.Transports.Internal;
using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Amqp.Transports;

/// <summary>
/// Represents an AMQP server transport layered on top of a carrier server transport.
/// </summary>
public sealed class AmqpServerTransport : ServerTransport<AmqpConnection>
{
    private readonly ITransport _transport;
    private readonly AmqpTransportOptions _options;
    private readonly List<AmqpConnection> _connections;
    private bool _isDisposed;

    /// <summary>
    /// Initializes a new AMQP server transport.
    /// </summary>
    /// <param name="transport">The lower-level carrier transport.</param>
    /// <param name="options">The AMQP transport options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="transport"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the carrier transport is not a server transport.</exception>
    public AmqpServerTransport(ITransport transport, AmqpTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transport);

        if (transport.Kind != TransportKind.Server)
        {
            throw new ArgumentException("The AMQP server transport requires a carrier transport configured as a server.", nameof(transport));
        }

        _transport = transport;
        _options = options ?? new AmqpTransportOptions();
        _connections = new List<AmqpConnection>();
    }

    /// <inheritdoc />
    public override TransportProtocol Protocol => TransportProtocol.Amqp;

    /// <summary>
    /// Gets the active AMQP connections accepted by this transport.
    /// </summary>
    public IReadOnlyCollection<AmqpConnection> Connections => _connections.AsReadOnly();

    /// <inheritdoc />
    public override async Task<AmqpConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, nameof(AmqpServerTransport));

        ITransportConnection connection = await _transport.InitializeAsync(cancellationToken).ConfigureAwait(false);
        AmqpTransportConnection amqpConnection = new(connection, Id, Kind, _options);

        amqpConnection.OnDispose = () => _connections.Remove(amqpConnection);

        _connections.Add(amqpConnection);

        return amqpConnection;
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
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

        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
