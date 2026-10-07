using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The <em>extended CONNECT</em> capability of the current exchange (RFC 8441 for HTTP/2, RFC 9220
/// for HTTP/3): the protocol the client asked to bootstrap, and the accept call that turns the
/// exchange's stream into a duplex tunnel for it.
/// </summary>
/// <remarks>
/// <para>
/// An extended CONNECT is a <c>CONNECT</c> request that also carries the <c>:protocol</c>
/// pseudo-header, plus <c>:scheme</c>, <c>:path</c>, and <c>:authority</c>. A client uses it to run
/// another protocol over one HTTP/2 or HTTP/3 stream; WebSocket (<c>:protocol = websocket</c>) is
/// the common case. The server transport (<c>Assimalign.Cohesion.Http.Connections</c>) installs this
/// feature on every exchange that is a valid extended CONNECT and on no other exchange. Read it as
/// <c>context.ExtendedConnect</c> (<c>Assimalign.Cohesion.Http.ExtendedConnect</c>).
/// </para>
/// <para>
/// The contract lives in the protocol core because its producer is the transport itself, which
/// references no feature package — the same placement as <see cref="IHttpTlsConnectionFeature"/>.
/// The implementation is internal to the transport.
/// </para>
/// <para>
/// <see cref="AcceptAsync"/> answers the request with <c>200</c> and surrenders the stream:
/// </para>
/// <list type="bullet">
/// <item><description>The response head carries the headers the application set on
/// <see cref="IHttpContext.Response"/> before accepting, with the fields a tunnel cannot carry
/// removed: <c>Content-Length</c> and <c>Transfer-Encoding</c> (RFC 9110 §9.3.6) and the
/// connection-specific fields (RFC 9113 §8.2.2, RFC 9114 §4.2). Any status the application set is
/// replaced by <c>200</c>, and a body it wrote to the response is discarded.</description></item>
/// <item><description>Reads return the client's <c>DATA</c> as it arrives and return 0 once the
/// client ends its side (HTTP/2 <c>END_STREAM</c>, HTTP/3 FIN).</description></item>
/// <item><description>Writes go out as <c>DATA</c> at once, unbuffered and paced by the peer's flow
/// control: a write completes when its octets are on the wire, and waits while the peer's windows
/// are exhausted.</description></item>
/// <item><description>Disposing the stream ends the server's side (HTTP/2 <c>END_STREAM</c>, HTTP/3
/// FIN). The client may still send until it ends its own side.</description></item>
/// <item><description>A peer reset or the loss of the connection faults pending and later reads and
/// writes with an <see cref="IOException"/>.</description></item>
/// </list>
/// <para>
/// Accepting takes the exchange over, as an HTTP/1.1 protocol upgrade does: the transport no longer
/// writes the application's response for the exchange, and the exchange interceptors' response-head
/// and after-response hooks do not run for it. The tunnel lasts as long as the exchange: when the
/// application's handler returns, the transport ends a tunnel the application left open, and a
/// cancelled exchange (<see cref="IHttpContext.Cancel"/>) resets the stream instead.
/// </para>
/// <para>
/// The stream carries raw octets. Framing the inner protocol is the caller's concern; for WebSocket,
/// <c>System.Net.WebSockets.WebSocket.CreateFromStream</c> runs over it.
/// </para>
/// </remarks>
public interface IHttpExtendedConnectFeature : IHttpFeature
{
    /// <summary>
    /// Gets the value of the <c>:protocol</c> pseudo-header the client requested (for example
    /// <c>websocket</c>). Never <see langword="null"/> or empty.
    /// </summary>
    string Protocol { get; }

    /// <summary>
    /// Accepts the extended CONNECT: sends a <c>200</c> response head without ending the stream and
    /// returns the exchange's stream as a duplex tunnel.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels writing the response head.</param>
    /// <returns>
    /// The duplex tunnel. The caller owns it and disposes it to end the server's side of the stream.
    /// </returns>
    /// <exception cref="System.InvalidOperationException">
    /// The tunnel was already accepted for this exchange, the response has already started, or the
    /// exchange was cancelled.
    /// </exception>
    /// <exception cref="IOException">
    /// The peer reset the stream or the connection closed before the response head was written.
    /// </exception>
    /// <exception cref="System.OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the response head was written; the
    /// exchange is then reset when it ends.
    /// </exception>
    ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken = default);
}
