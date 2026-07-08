using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Builds the protocol-specific <see cref="HttpConnection"/> for an accepted
/// stream transport connection (HTTP/1.1, HTTP/2). Each stream-protocol
/// registration carries one implementation, so the accept loop dispatches to a
/// captured factory instead of switching on the protocol per connection.
/// </summary>
internal abstract class HttpConnectionFactory
{
    /// <summary>
    /// Wraps <paramref name="connection"/> in the protocol-specific connection.
    /// </summary>
    /// <param name="connection">The accepted transport connection.</param>
    /// <param name="isSecure">Whether the transport reports a TLS-secured connection.</param>
    /// <returns>The HTTP connection.</returns>
    public abstract HttpConnection Create(IConnection connection, bool isSecure);
}
