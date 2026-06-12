using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Transports;

/// <summary>
/// Represents a protocol-specific HTTP connection layered on top of an accepted transport connection.
/// </summary>
/// <remarks>
/// HTTP consumes the <c>Assimalign.Cohesion.Connections</c> contracts rather than extending them:
/// an HTTP connection wraps a live <see cref="IConnection"/> (HTTP/1.1 and HTTP/2) or
/// <see cref="IMultiplexedConnection"/> (HTTP/3) and projects HTTP semantics over it.
/// </remarks>
public interface IHttpConnection : IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier of the underlying transport connection.
    /// </summary>
    ConnectionId Id { get; }

    /// <summary>
    /// Gets the current lifecycle state of the underlying transport connection.
    /// </summary>
    ConnectionState State { get; }

    /// <summary>
    /// Gets a token that is signaled when the underlying transport connection is closed or aborted.
    /// </summary>
    CancellationToken ConnectionClosed { get; }

    /// <summary>
    /// Aborts the underlying transport connection immediately, discarding any in-flight data.
    /// </summary>
    /// <param name="reason">An optional exception describing why the connection was aborted.</param>
    void Abort(Exception? reason = null);

    /// <summary>
    /// Opens the HTTP connection context used to receive requests and send responses.
    /// </summary>
    /// <returns>The opened connection context.</returns>
    IHttpConnectionContext Open();

    /// <summary>
    /// Opens the HTTP connection context used to receive requests and send responses.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the open operation.</param>
    /// <returns>The opened connection context.</returns>
    ValueTask<IHttpConnectionContext> OpenAsync(CancellationToken cancellationToken = default);
}
