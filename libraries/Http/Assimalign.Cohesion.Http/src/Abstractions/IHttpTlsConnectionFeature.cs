using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Describes the TLS session that secures the connection an exchange arrived on: the certificate the
/// client presented, the TLS protocol version, the cipher suite, and the application protocol ALPN
/// selected.
/// </summary>
/// <remarks>
/// <para>
/// The server transport attaches this feature to <see cref="IHttpContext.Features"/> on every exchange
/// that arrived over TLS — HTTP/1.1 and HTTP/2 over a TLS connection, and HTTP/3, whose QUIC transport
/// carries TLS itself — and attaches none to a cleartext exchange. Read it through the
/// <see cref="HttpTlsConnectionExtensions"/> member <c>context.TlsConnection</c>. The values belong to
/// the connection, not the request: every exchange on a connection observes the same session.
/// </para>
/// <para>
/// A client certificate is present only when the server requested one during the handshake (RFC 8446
/// §4.3.2) and the client sent one; the server's TLS options decide whether one is requested, required,
/// and how it is validated. Over HTTP/2 the certificate can be requested only in the handshake, never
/// afterwards (RFC 9113 §9.2.3). Authenticating a request from the certificate is a decision for the
/// application, which this feature only informs.
/// </para>
/// </remarks>
public interface IHttpTlsConnectionFeature : IHttpFeature
{
    /// <summary>
    /// Gets the certificate the client presented during the handshake, or <see langword="null"/> when
    /// it presented none.
    /// </summary>
    /// <remarks>
    /// The connection owns the certificate and disposes it when the connection closes, so code that
    /// keeps it beyond the exchange copies it first.
    /// </remarks>
    X509Certificate2? ClientCertificate { get; }

    /// <summary>
    /// Gets the TLS protocol version the handshake negotiated (for example
    /// <see cref="SslProtocols.Tls13"/>).
    /// </summary>
    SslProtocols Protocol { get; }

    /// <summary>
    /// Gets the cipher suite the handshake negotiated.
    /// </summary>
    TlsCipherSuite CipherSuite { get; }

    /// <summary>
    /// Gets the application protocol the handshake selected through ALPN (RFC 7301) — <c>http/1.1</c>,
    /// <c>h2</c>, or <c>h3</c> — or <see langword="default"/> when the client offered none.
    /// </summary>
    SslApplicationProtocol ApplicationProtocol { get; }
}
