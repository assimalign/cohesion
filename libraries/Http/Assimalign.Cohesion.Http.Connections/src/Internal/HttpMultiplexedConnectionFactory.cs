using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Builds the <see cref="HttpConnection"/> for an accepted multiplexed transport
/// connection (HTTP/3 over QUIC). The HTTP/3 registration carries one
/// implementation, so the accept loop dispatches to a captured factory instead
/// of a dedicated create method.
/// </summary>
internal abstract class HttpMultiplexedConnectionFactory
{
    /// <summary>
    /// Wraps <paramref name="connection"/> in the HTTP/3 connection.
    /// </summary>
    /// <param name="connection">The accepted multiplexed transport connection.</param>
    /// <param name="isSecure">Whether the transport reports a TLS-secured connection.</param>
    /// <returns>The HTTP connection.</returns>
    public abstract HttpConnection Create(IMultiplexedConnection connection, bool isSecure);
}
