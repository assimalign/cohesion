using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Accepts inbound multiplexed connections bound to a local endpoint (the server side of a multiplexed transport).
/// </summary>
public interface IMultiplexedConnectionListener : IAsyncDisposable
{
    /// <summary>
    /// Gets the configured local endpoint before binding and the acquired endpoint afterward.
    /// </summary>
    EndPoint EndPoint { get; }

    /// <summary>
    /// Gets the capabilities of connections produced by this listener.
    /// </summary>
    ConnectionCapabilities Capabilities { get; }

    /// <summary>
    /// Binds the listener to its configured local endpoint.
    /// </summary>
    /// <remarks>
    /// Successful repeated calls are idempotent. Disposing the listener terminally releases the
    /// endpoint; a resource-owning listener cannot be rebound after disposal.
    /// </remarks>
    /// <param name="cancellationToken">A token to cancel the bind operation.</param>
    /// <returns>A task that completes when the endpoint has been acquired and the listener is ready to accept connections.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the bind operation is canceled.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the listener has been disposed.</exception>
    ValueTask BindAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts the next inbound multiplexed connection.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the accept operation.</param>
    /// <returns>The accepted <see cref="IMultiplexedConnection"/>.</returns>
    ValueTask<IMultiplexedConnection> AcceptAsync(CancellationToken cancellationToken = default);
}
