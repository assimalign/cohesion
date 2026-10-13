using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Accepts inbound connections and observes the lifetime of the connections it hands to callers.
/// </summary>
public interface IConnectionListener : IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier for this listener.
    /// </summary>
    ListenerId Id { get; }

    /// <summary>
    /// Gets a live view of accepted connections whose lifetime has not ended.
    /// </summary>
    /// <remarks>
    /// Entries appear before accept returns and leave when their connection-closed token fires.
    /// Enumeration tolerates concurrent changes; it is not an atomic snapshot. Only the accepting
    /// code reads or writes a connection. Disposing the listener does not close accepted connections.
    /// The listener holds a strong reference to each entry until it closes.
    /// </remarks>
    IReadOnlyCollection<IConnection> Connections { get; }

    /// <summary>
    /// Binds the listener to its configured local endpoints.
    /// </summary>
    /// <remarks>
    /// Successful repeated calls are idempotent. Disposing the listener terminally releases the
    /// endpoints; a resource-owning listener cannot be rebound after disposal.
    /// </remarks>
    /// <param name="cancellationToken">A token to cancel the bind operation.</param>
    /// <returns>A task that completes when all endpoints have been acquired and the listener is ready to accept connections.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the bind operation is canceled.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the listener has been disposed.</exception>
    ValueTask BindAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts the next inbound connection.
    /// </summary>
    /// <remarks>
    /// The returned connection is ready to use: any layer composed onto the listener has already upgraded
    /// it. A failure that belongs to one inbound connection, such as a handshake that fails or times out,
    /// is the listener's to contain: it releases that connection and goes on to the next, so one peer can
    /// never stop the listener (see <see cref="ConnectionLayerExtensions"/> for how a layered listener
    /// does it). An exception from this method therefore means the listener itself can produce no more
    /// connections (it was disposed or its endpoint failed), or <paramref name="cancellationToken"/> was
    /// canceled.
    /// </remarks>
    /// <param name="cancellationToken">A token to cancel the accept operation.</param>
    /// <returns>The accepted <see cref="IConnection"/>.</returns>
    ValueTask<IConnection> AcceptAsync(CancellationToken cancellationToken = default);
}
