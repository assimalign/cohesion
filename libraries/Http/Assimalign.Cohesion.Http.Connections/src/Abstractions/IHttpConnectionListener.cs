using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections;

/// <summary>
/// Accepts transport connections and projects them into protocol-specific HTTP connections.
/// </summary>
public interface IHttpConnectionListener : IAsyncDisposable
{
    /// <summary>
    /// Gets the HTTP protocols served by this listener.
    /// </summary>
    HttpProtocol Protocols { get; }

    /// <summary>
    /// Binds every configured transport listener to its endpoint.
    /// </summary>
    /// <remarks>
    /// The returned operation completes only after every endpoint is ready to accept connections.
    /// Repeated calls are idempotent. If one listener cannot bind, listeners that were already
    /// bound are released before the original failure is rethrown.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token for the bind operation.</param>
    /// <returns>A task that represents the bind operation.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the bind operation is canceled.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the listener has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no transport listener is configured.</exception>
    ValueTask BindAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts the next available HTTP connection from the configured connection listeners.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the accept operation.</param>
    /// <returns>The next accepted HTTP connection.</returns>
    Task<IHttpConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default);
}
