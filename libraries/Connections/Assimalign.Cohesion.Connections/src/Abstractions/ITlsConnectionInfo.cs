using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Exposes what the TLS handshake of a secured connection negotiated, so an application protocol can
/// act on it without depending on the layer or driver that ran the handshake.
/// </summary>
/// <remarks>
/// <para>
/// A connection that terminates TLS implements this interface beside its connection contract: the
/// connection a TLS <see cref="IConnectionLayer"/> returns from
/// <see cref="IConnectionLayer.UpgradeAsync(IConnection, System.Threading.CancellationToken)"/>, and a
/// multiplexed connection whose transport carries TLS itself (QUIC, RFC 9001). A consumer discovers it
/// with a type test on the connection it holds; a connection without it is either plaintext or secured
/// by a layer that does not report its handshake.
/// </para>
/// <para>
/// The values are fixed once the handshake completes, which is before the connection is handed to a
/// consumer. A layer composed above the TLS layer that returns a new connection hides this interface
/// unless that connection implements it too and forwards to the connection it wraps; a pass-through
/// layer that returns the connection it was given keeps it visible.
/// </para>
/// </remarks>
public interface ITlsConnectionInfo
{
    /// <summary>
    /// Gets the application protocol the handshake selected through Application-Layer Protocol
    /// Negotiation (ALPN, RFC 7301), or <see langword="default"/> when no protocol was selected
    /// because the peer offered none.
    /// </summary>
    /// <remarks>
    /// The selection belongs to the server. RFC 7301 §3.2 has it choose its most preferred protocol
    /// among those the client offered, and refuse a client it shares none with through a
    /// <c>no_application_protocol</c> alert. The value is whatever the platform TLS stack reports
    /// once the handshake completes.
    /// </remarks>
    SslApplicationProtocol ApplicationProtocol { get; }

    /// <summary>
    /// Gets the TLS protocol version the handshake negotiated (for example
    /// <see cref="SslProtocols.Tls13"/>).
    /// </summary>
    SslProtocols TlsProtocol { get; }

    /// <summary>
    /// Gets the cipher suite the handshake negotiated.
    /// </summary>
    TlsCipherSuite CipherSuite { get; }

    /// <summary>
    /// Gets the certificate the remote peer presented during the handshake, or
    /// <see langword="null"/> when it presented none.
    /// </summary>
    /// <remarks>
    /// On a server-side connection this is the client certificate, which a server receives only when
    /// it requested one in the handshake (RFC 8446 §4.3.2); on a client-side connection it is the
    /// server's certificate. The connection owns the certificate and disposes it with itself, so a
    /// consumer that keeps it beyond the connection's lifetime copies it first.
    /// </remarks>
    X509Certificate2? RemoteCertificate { get; }
}
