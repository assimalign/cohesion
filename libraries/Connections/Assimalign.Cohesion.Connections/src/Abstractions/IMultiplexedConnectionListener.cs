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
    /// Gets the local endpoint the listener is bound to.
    /// </summary>
    EndPoint EndPoint { get; }

    /// <summary>
    /// Gets the capabilities of connections produced by this listener.
    /// </summary>
    ConnectionCapabilities Capabilities { get; }

    /// <summary>
    /// Accepts the next inbound multiplexed connection.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the accept operation.</param>
    /// <returns>The accepted <see cref="IMultiplexedConnection"/>.</returns>
    ValueTask<IMultiplexedConnection> AcceptAsync(CancellationToken cancellationToken = default);
}
