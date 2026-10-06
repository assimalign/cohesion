using System.Net.Security;

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
}
