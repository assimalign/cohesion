using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Builds the protocol-specific <see cref="HttpConnection"/> for an accepted
/// stream transport connection (HTTP/1.1, HTTP/2). Each stream-protocol
/// registration carries one implementation, so the accept loop dispatches to a
/// captured factory instead of switching on the protocol per connection. A
/// registration that serves both stream protocols carries
/// <see cref="HttpAlpnConnectionFactory"/>, which picks between the HTTP/1.1 and
/// HTTP/2 factories from the protocol each connection's TLS handshake negotiated.
/// </summary>
internal abstract class HttpConnectionFactory
{
    /// <summary>
    /// The precomputed RFC 7838 <c>Alt-Svc</c> response-header value this stream protocol injects on
    /// its responses to advertise the listener's HTTP/3 endpoint, or <see langword="null"/> when
    /// advertisement is disabled or no HTTP/3 listener is registered. Set once by
    /// <see cref="HttpConnectionListener"/> after every listener has been materialized (so the h3
    /// endpoint is known) and before the first connection is accepted; read on the response path.
    /// </summary>
    public virtual string? AltSvcHeaderValue { get; set; }

    /// <summary>
    /// Wraps <paramref name="connection"/> in the protocol-specific connection.
    /// </summary>
    /// <param name="connection">The accepted transport connection.</param>
    /// <param name="isSecure">Whether the transport reports a TLS-secured connection.</param>
    /// <returns>
    /// The HTTP connection, or <see langword="null"/> when this registration cannot serve the
    /// connection (its TLS handshake negotiated an application protocol the registration does not
    /// speak); the accept loop then closes the connection and keeps accepting.
    /// </returns>
    public abstract HttpConnection? Create(IConnection connection, bool isSecure);
}
