using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.InMemory;

/// <summary>
/// Dials the <see cref="InMemoryMultiplexedConnectionListener"/> it is bound to, establishing outbound
/// in-memory multiplexed connections (the client side of the in-memory multiplexed transport).
/// </summary>
/// <remarks>
/// Each call to <see cref="ConnectAsync(EndPoint, CancellationToken)"/> creates a fresh cross-wired
/// multiplexed connection pair and returns the client end; the matching server end is queued on the
/// bound listener for its next accept.
/// </remarks>
public sealed class InMemoryMultiplexedConnectionFactory : MultiplexedConnectionFactory
{
    private readonly InMemoryMultiplexedConnectionListener _listener;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryMultiplexedConnectionFactory"/> class bound to a listener.
    /// </summary>
    /// <param name="listener">The listener this factory dials.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="listener"/> is <see langword="null"/>.</exception>
    public InMemoryMultiplexedConnectionFactory(InMemoryMultiplexedConnectionListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        _listener = listener;
    }

    /// <inheritdoc />
    public override ConnectionCapabilities Capabilities => _listener.Capabilities;

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="endPoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ConnectionAbortedException">Thrown when the bound listener has been disposed.</exception>
    public override ValueTask<MultiplexedConnection> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endPoint);

        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_listener.Connect());
    }
}
