using System.Net.Security;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Serves HTTP/1.1 and HTTP/2 on one TLS listener: each accepted connection is handed to the HTTP/2
/// or the HTTP/1.1 factory according to the application protocol its handshake negotiated through
/// ALPN (RFC 7301; RFC 9113 §3.2 identifies HTTP/2 over TLS as <c>h2</c>).
/// </summary>
/// <remarks>
/// <para>
/// The negotiated protocol is read through <see cref="ITlsConnectionInfo"/>, which the connection that
/// ran the handshake implements; this package never runs TLS itself. <c>h2</c> selects HTTP/2;
/// <c>http/1.1</c> selects HTTP/1.1, and so does a connection that negotiated no protocol (a client
/// that sent no ALPN extension, or a connection that does not report its handshake), because HTTP/1.1
/// is what a client that does not negotiate expects of an <c>https</c> origin.
/// </para>
/// <para>
/// Any other negotiated protocol is one the server offered but this registration does not speak (an
/// application-supplied ALPN list can name others, such as <c>acme-tls/1</c>). RFC 7301 §3.2 binds
/// the connection to the protocol the handshake selected, so neither HTTP version may be spoken on
/// it: <see cref="Create"/> returns <see langword="null"/> and the accept loop closes the connection
/// and keeps accepting.
/// </para>
/// </remarks>
internal sealed class HttpAlpnConnectionFactory : HttpConnectionFactory
{
    private readonly Http1ConnectionFactory _http1;
    private readonly Http2ConnectionFactory _http2;

    public HttpAlpnConnectionFactory(Http1ConnectionFactory http1, Http2ConnectionFactory http2)
    {
        _http1 = http1;
        _http2 = http2;
    }

    /// <summary>
    /// The <c>Alt-Svc</c> advertisement, shared by both protocols this registration serves so an
    /// HTTP/1.1 and an HTTP/2 response from the same endpoint advertise the same h3 alternative.
    /// </summary>
    public override string? AltSvcHeaderValue
    {
        get => _http1.AltSvcHeaderValue;
        set
        {
            _http1.AltSvcHeaderValue = value;
            _http2.AltSvcHeaderValue = value;
        }
    }

    public override HttpConnection? Create(IConnection connection, bool isSecure)
    {
        SslApplicationProtocol protocol = connection is ITlsConnectionInfo tls
            ? tls.ApplicationProtocol
            : default;

        if (protocol == SslApplicationProtocol.Http2)
        {
            return _http2.Create(connection, isSecure);
        }

        if (protocol.Protocol.IsEmpty || protocol == SslApplicationProtocol.Http11)
        {
            return _http1.Create(connection, isSecure);
        }

        return null;
    }
}
