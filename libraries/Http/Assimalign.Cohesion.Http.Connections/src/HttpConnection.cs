using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections;

/// <summary>
/// Serves as the base for protocol-specific HTTP connections produced by
/// <see cref="HttpConnectionListener"/>.
/// </summary>
/// <remarks>
/// The base intentionally holds no transport reference of its own: HTTP/1.1 and HTTP/2
/// connections wrap an <see cref="IConnection"/> while HTTP/3 wraps an
/// <see cref="IMultiplexedConnection"/>, and each derived type forwards the identity and
/// lifecycle members to the connection it owns.
/// </remarks>
public abstract class HttpConnection : IHttpConnection
{
    internal HttpConnection(bool isSecure)
    {
        IsSecure = isSecure;
    }

    /// <summary>
    /// Gets whether the underlying transport connection is secured (derived from the
    /// producing listener's <see cref="ConnectionCapabilities.Security"/>).
    /// </summary>
    protected bool IsSecure { get; }

    /// <inheritdoc />
    public abstract ConnectionId Id { get; }

    /// <inheritdoc />
    public abstract ConnectionState State { get; }

    /// <inheritdoc />
    public abstract CancellationToken ConnectionClosed { get; }

    /// <inheritdoc />
    public abstract void Abort(Exception? reason = null);

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    /// <summary>
    /// Opens the HTTP connection context used to receive requests and send responses.
    /// </summary>
    /// <returns>The opened connection context.</returns>
    public abstract HttpConnectionContext Open();

    /// <summary>
    /// Opens the HTTP connection context used to receive requests and send responses.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the open operation.</param>
    /// <returns>The opened connection context.</returns>
    public abstract ValueTask<HttpConnectionContext> OpenAsync(CancellationToken cancellationToken = default);

    IHttpConnectionContext IHttpConnection.Open()
    {
        return Open();
    }

    async ValueTask<IHttpConnectionContext> IHttpConnection.OpenAsync(CancellationToken cancellationToken)
    {
        return await OpenAsync(cancellationToken).ConfigureAwait(false);
    }
}
