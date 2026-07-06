using System;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.InMemory.Internal;

namespace Assimalign.Cohesion.Connections.InMemory;

/// <summary>
/// Accepts inbound in-memory multiplexed connections dialed by an
/// <see cref="InMemoryMultiplexedConnectionFactory"/> bound to this listener (the server side of the
/// in-memory multiplexed transport).
/// </summary>
/// <remarks>
/// Each dial creates a fresh cross-wired multiplexed connection pair: the client end is returned to the
/// dialer and the server end is queued here for the next <see cref="AcceptAsync(CancellationToken)"/>.
/// </remarks>
public sealed class InMemoryMultiplexedConnectionListener : MultiplexedConnectionListener
{
    private readonly Channel<MultiplexedConnection> _inbound = Channel.CreateUnbounded<MultiplexedConnection>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private readonly InMemoryEndPoint _endPoint;
    private readonly ConnectionCapabilities _capabilities;
    private readonly Lock _gate = new();
    private bool _isDisposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryMultiplexedConnectionListener"/> class.
    /// </summary>
    /// <param name="endPoint">The endpoint the listener is bound to, or <see langword="null"/> for the default in-memory endpoint.</param>
    /// <param name="capabilities">
    /// The capabilities connections produced by this listener advertise, or <see langword="null"/> to use
    /// <see cref="InMemoryMultiplexedConnectionPair.DefaultCapabilities"/>.
    /// </param>
    public InMemoryMultiplexedConnectionListener(InMemoryEndPoint? endPoint = null, ConnectionCapabilities? capabilities = null)
    {
        _endPoint = endPoint ?? new InMemoryEndPoint(InMemoryEndPoint.DefaultName);
        _capabilities = capabilities ?? InMemoryMultiplexedConnectionPair.DefaultCapabilities;
    }

    /// <inheritdoc />
    public override EndPoint EndPoint => _endPoint;

    /// <inheritdoc />
    public override ConnectionCapabilities Capabilities => _capabilities;

    /// <inheritdoc />
    public override async ValueTask<MultiplexedConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new OperationCanceledException("The in-memory multiplexed listener has been disposed.", cancellationToken);
        }
    }

    /// <summary>
    /// Creates an <see cref="InMemoryMultiplexedConnectionFactory"/> that dials this listener.
    /// </summary>
    /// <returns>A new factory bound to this listener.</returns>
    public InMemoryMultiplexedConnectionFactory CreateFactory() => new(this);

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_isDisposed)
            {
                return ValueTask.CompletedTask;
            }

            _isDisposed = true;
            _inbound.Writer.TryComplete();
        }

        while (_inbound.Reader.TryRead(out MultiplexedConnection? pending))
        {
            _ = pending.DisposeAsync();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Dials this listener, producing a cross-wired multiplexed connection pair: the server end is queued
    /// for the next accept and the client end is returned to the caller.
    /// </summary>
    /// <returns>The client end of the newly created multiplexed connection.</returns>
    /// <exception cref="ConnectionAbortedException">Thrown when the listener has been disposed.</exception>
    internal MultiplexedConnection Connect()
    {
        InMemoryEndPoint clientEndPoint = InMemoryEndPoint.CreateEphemeral();

        (MultiplexedConnection client, MultiplexedConnection server) =
            InMemoryMultiplexedConnectionPair.Create(_capabilities, clientEndPoint, _endPoint);

        lock (_gate)
        {
            if (!_isDisposed && _inbound.Writer.TryWrite(server))
            {
                return client;
            }
        }

        _ = client.DisposeAsync();
        _ = server.DisposeAsync();

        throw new ConnectionAbortedException("The in-memory multiplexed listener has been disposed and is no longer accepting connections.");
    }
}
