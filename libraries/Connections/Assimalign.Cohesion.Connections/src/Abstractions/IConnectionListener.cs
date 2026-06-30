using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Accepts inbound single-stream connections bound to a local endpoint (the server side of a transport).
/// </summary>
public interface IConnectionListener : IAsyncDisposable
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
    /// Accepts the next inbound connection.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the accept operation.</param>
    /// <returns>The accepted <see cref="IConnection"/>.</returns>
    ValueTask<IConnection> AcceptAsync(CancellationToken cancellationToken = default);
}
