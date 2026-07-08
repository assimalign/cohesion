using System.IO;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A transport capability, surfaced through
/// <see cref="HttpResponseInterceptorContext.ConnectionTakeover"/>, that lets a feature package
/// take an exchange's connection out of the HTTP request/response loop and drive the raw duplex
/// byte stream directly.
/// </summary>
/// <remarks>
/// <para>
/// This is the generic seam behind HTTP/1.1 connection transitions — an RFC 9110 §7.8 protocol
/// upgrade (<c>101 Switching Protocols</c>) or an RFC 9110 §9.3.6 <c>CONNECT</c> tunnel. The
/// transport knows nothing about those semantics: it only offers the capability, and a feature
/// package (for example <c>Assimalign.Cohesion.Http.ProtocolUpgrade</c>) decides when to exercise
/// it and what bytes to speak afterward. This mirrors how
/// <see cref="HttpResponseInterceptorContext.ResponseBody"/> exposes the framed response sink to
/// streaming features without the transport referencing them.
/// </para>
/// <para>
/// The capability is populated only where a takeover is physically possible: an HTTP/1.1 exchange
/// owns its whole connection, so the HTTP/1.1 transport offers it. HTTP/2 and HTTP/3 exchanges are
/// multiplexed streams over a shared connection — those protocols removed the <c>Upgrade</c>
/// mechanism (RFC 9113 §8.6, RFC 9114 §4.2) and bootstrap other protocols via extended CONNECT
/// instead — so <see cref="HttpResponseInterceptorContext.ConnectionTakeover"/> is
/// <see langword="null"/> there.
/// </para>
/// </remarks>
public interface IHttpConnectionTakeover
{
    /// <summary>
    /// Takes over the connection. From this point the transport suppresses its own response for
    /// the exchange (the normal send becomes a no-op, so nothing is double-written onto what is
    /// now a raw byte stream) and stops reusing the connection for further HTTP requests (the
    /// keep-alive loop ends after the current exchange).
    /// </summary>
    /// <returns>
    /// The raw duplex transport stream, positioned at the first octet after the parsed request —
    /// octets the peer pipelined behind the request head are readable, never consumed by the
    /// HTTP parser. The caller owns all subsequent I/O on the stream; the transport still owns
    /// the underlying connection's disposal when the server's connection scope ends.
    /// </returns>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when the connection has already been taken over — the capability is one-shot so two
    /// features cannot both claim the same connection.
    /// </exception>
    Stream TakeOver();
}
