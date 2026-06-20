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
    /// Accepts the next available HTTP connection from the configured connection listeners.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the accept operation.</param>
    /// <returns>The next accepted HTTP connection.</returns>
    Task<IHttpConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default);
}
