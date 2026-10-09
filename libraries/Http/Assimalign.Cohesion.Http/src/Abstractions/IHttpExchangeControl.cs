using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The transport's per-exchange <b>wire mechanisms</b> that fall outside the normal response path,
/// surfaced through <see cref="HttpExchangeInterceptorResponseContext.Control"/>: interim (<c>1xx</c>)
/// writes ahead of the final response, the raw-connection takeover that HTTP/1.1 protocol upgrades /
/// <c>CONNECT</c> tunnels need, and the stream tunnel that an HTTP/2 or HTTP/3 extended
/// <c>CONNECT</c> needs. It is the single generic surface feature packages wrap into
/// application-facing features — one contract instead of a per-capability contract for every wire
/// action.
/// </summary>
/// <remarks>
/// <para>
/// One generic control deliberately replaces per-capability contracts (the former
/// <c>IHttpConnectionTakeover</c> and <c>IHttpInterimResponseWriter</c>, and the extended CONNECT
/// accept that core carried for a while): feature packages — protocol upgrade, interim responses,
/// extended CONNECT, and future compression features — compose from this contract plus the
/// <see cref="IHttpExchangeInterceptor"/> lifecycle hooks, so tapping a new wire mechanism never
/// requires a new core abstraction or new transport plumbing. The transport implements this per
/// protocol version and owns every wire encoding behind it; feature packages reference only the
/// protocol core.
/// </para>
/// <para>
/// <b>This control carries mechanisms, not decisions.</b> The layering rule: mechanisms live at
/// the lowest level that can observe what they need; decisions live at the application level.
/// Feature packages wrap these mechanisms into typed features (<c>context.Upgrade</c>,
/// <c>context.InterimResponse</c>, <c>context.ExtendedConnect</c>), and the <em>application</em>
/// decides when to exercise them.
/// Aborting an exchange is likewise an application decision and has no member here — it is
/// <see cref="IHttpContext.Cancel"/> / <see cref="IHttpContext.CancelAsync"/>, which the transport
/// honors at its lifecycle checkpoints with the wire behavior appropriate to its version (HTTP/2
/// resets the stream, HTTP/3 resets the request stream, HTTP/1.1 writes no response and ends the
/// connection after the exchange).
/// </para>
/// <para>
/// Capability probes (<see cref="CanWriteInterimResponse"/>, <see cref="CanTakeOver"/>,
/// <see cref="CanAcceptTunnel"/>) are the report-don't-throw discovery path: a caller checks them
/// and learns the exchange state without provoking an exception. The imperative members throw only
/// on genuine misuse (taking over an exchange that cannot be taken over, writing an interim
/// response after the final response started, accepting a tunnel twice) or on a stream the peer
/// already reset. The control is exchange-scoped and is not thread-safe; it must be driven from the
/// exchange's handling flow (interceptor hooks, the application handler, features it installed).
/// </para>
/// </remarks>
public interface IHttpExchangeControl
{
    /// <summary>
    /// Gets whether the final response has started — its head has been (or is being) committed to
    /// the wire. Once <see langword="true"/>, interim responses can no longer precede it and the
    /// response status/headers are effectively locked.
    /// </summary>
    bool HasResponseStarted { get; }

    /// <summary>
    /// Gets whether an interim (<c>1xx</c>) response can still be emitted for this exchange — the
    /// transport supports interim responses, the final response has not started, and the exchange
    /// has not been aborted.
    /// </summary>
    bool CanWriteInterimResponse { get; }

    /// <summary>
    /// Emits a single interim (<c>1xx</c>) response ahead of the final response (RFC 9110 §15.2) —
    /// most usefully <c>100 Continue</c> (§10.1.1) and <c>103 Early Hints</c> (RFC 8297). The
    /// transport owns the per-version wire encoding: an HTTP/1.1 status line, an HTTP/2 HEADERS
    /// block without <c>END_STREAM</c> (RFC 9113 §8.1), an HTTP/3 field section on the request
    /// stream (RFC 9114 §4.1) — each with no body and no <c>Content-Length</c>. May be called
    /// repeatedly; every interim response precedes the final one.
    /// </summary>
    /// <param name="statusCode">
    /// The interim status code. MUST be in the <c>1xx</c> range (100–199) and MUST NOT be
    /// <c>101 Switching Protocols</c> — that transition is a connection takeover
    /// (<see cref="TakeOver"/>), owned by the protocol-upgrade feature package.
    /// </param>
    /// <param name="headers">
    /// The interim response fields, or <see langword="null"/> for none. A <c>100 Continue</c>
    /// carries no fields; a <c>103 Early Hints</c> typically carries only
    /// <see cref="HttpHeaderKey.Link"/> fields.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the interim response has been flushed to the transport.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException">
    /// <paramref name="statusCode"/> is outside the <c>1xx</c> range, or is <c>101</c>.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// The final response has already started, so an interim response can no longer precede it.
    /// </exception>
    ValueTask WriteInterimResponseAsync(
        HttpStatusCode statusCode,
        IHttpHeaderCollection? headers = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets whether this exchange's connection can be taken over. Only an HTTP/1.1 exchange owns
    /// its whole connection; HTTP/2 and HTTP/3 exchanges are multiplexed streams over a shared
    /// connection — those protocols removed the <c>Upgrade</c> mechanism (RFC 9113 §8.6,
    /// RFC 9114 §4.2) and bootstrap other protocols via extended CONNECT instead, which
    /// <see cref="AcceptTunnelAsync"/> serves per stream — so takeover reports
    /// <see langword="false"/> there. Also <see langword="false"/> once the exchange has already
    /// been taken over, the final response has started, or the exchange has been aborted.
    /// </summary>
    bool CanTakeOver { get; }

    /// <summary>
    /// Takes over the exchange's connection — the transport gives up control: it suppresses its
    /// own response for the exchange (the normal send becomes a no-op, so nothing is
    /// double-written onto what is now a raw byte stream) and stops reusing the connection for
    /// further HTTP requests. This is the escape hatch RFC 9110 §7.8 protocol upgrades
    /// (<c>101 Switching Protocols</c>) and §9.3.6 <c>CONNECT</c> tunnels need. The takeover is
    /// one-shot and claims the connection <em>before</em> any transition byte is written, so two
    /// features can never fight over the same connection.
    /// </summary>
    /// <returns>
    /// The raw duplex transport stream, positioned at the first octet after the parsed request —
    /// octets the peer pipelined behind the request head are readable, never consumed by the HTTP
    /// parser. The caller owns all subsequent I/O on the stream; the transport still owns the
    /// underlying connection's disposal when the server's connection scope ends.
    /// </returns>
    /// <exception cref="System.InvalidOperationException">
    /// The exchange cannot be taken over (<see cref="CanTakeOver"/> is <see langword="false"/>):
    /// the protocol multiplexes exchanges over a shared connection, the exchange was already taken
    /// over (the capability is one-shot), the final response has started, or the exchange was
    /// aborted.
    /// </exception>
    Stream TakeOver();

    /// <summary>
    /// Gets whether this exchange's stream can be accepted as an extended CONNECT tunnel
    /// (<see cref="AcceptTunnelAsync"/>): the exchange is a validated HTTP/2 or HTTP/3 extended
    /// CONNECT (RFC 8441 §4, RFC 9220 §3; see <see cref="HttpExchangeInterceptorRequestContext.Protocol"/>),
    /// its tunnel has not already been accepted, its final response has not started, and it has not
    /// been cancelled. Always <see langword="false"/> on HTTP/1.1, whose <c>CONNECT</c> and upgrades
    /// take the whole connection over instead (<see cref="TakeOver"/>).
    /// </summary>
    /// <remarks>
    /// The probe does not report a stream the peer has already reset: that is learned from
    /// <see cref="AcceptTunnelAsync"/>, which fails with an <see cref="IOException"/>.
    /// </remarks>
    bool CanAcceptTunnel { get; }

    /// <summary>
    /// Accepts the exchange's extended CONNECT: answers it with <c>200</c> in a response head that
    /// does not end the stream, takes the exchange over, and returns the stream as a duplex tunnel
    /// (RFC 8441 §5, RFC 9220 §3). One-shot.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>The response head carries the headers already set on the exchange's
    /// response, with the fields a tunnel cannot carry removed: <c>Content-Length</c> and
    /// <c>Transfer-Encoding</c> (RFC 9110 §9.3.6) and the connection-specific fields (RFC 9113
    /// §8.2.2, RFC 9114 §4.2). Any status already set is replaced by <c>200</c>, and a body written
    /// to the response is discarded.</description></item>
    /// <item><description>Reads return the client's <c>DATA</c> as it arrives and return 0 once the
    /// client ends its side (HTTP/2 <c>END_STREAM</c>, HTTP/3 FIN).</description></item>
    /// <item><description>Writes go out as <c>DATA</c> at once, unbuffered and paced by the peer's
    /// flow control: a write completes when its octets are on the wire, and waits while the peer's
    /// windows are exhausted.</description></item>
    /// <item><description>Disposing the tunnel ends the server's side (HTTP/2 <c>END_STREAM</c>,
    /// HTTP/3 FIN). The client may still send until it ends its own side.</description></item>
    /// <item><description>A peer reset or the loss of the connection faults pending and later reads
    /// and writes with an <see cref="IOException"/>.</description></item>
    /// </list>
    /// <para>
    /// Accepting takes the exchange over, as <see cref="TakeOver"/> does for an HTTP/1.1 connection:
    /// the transport registers the tunnel before it writes the head, so it never writes the
    /// application's response for the exchange, and the
    /// <see cref="IHttpExchangeInterceptor.BeforeResponseHeadAsync"/> and
    /// <see cref="IHttpExchangeInterceptor.AfterResponseAsync"/> hooks do not run for it. The tunnel
    /// lasts as long as the exchange: when the application's handler returns, the transport ends a
    /// tunnel the application left open, and a cancelled exchange (<see cref="IHttpContext.Cancel"/>)
    /// resets the stream instead.
    /// </para>
    /// <para>
    /// The tunnel carries raw octets. Framing the inner protocol is the caller's concern; for
    /// WebSocket, <c>System.Net.WebSockets.WebSocket.CreateFromStream</c> runs over it.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">A token that cancels writing the response head.</param>
    /// <returns>
    /// The duplex tunnel. The caller owns it and disposes it to end the server's side of the stream.
    /// </returns>
    /// <exception cref="System.InvalidOperationException">
    /// The exchange is not an extended CONNECT on HTTP/2 or HTTP/3, its tunnel was already accepted,
    /// its response has already started, or it was cancelled.
    /// </exception>
    /// <exception cref="IOException">
    /// The peer reset the stream or the connection closed before the response head was written.
    /// </exception>
    /// <exception cref="System.OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the response head was written; the
    /// exchange is then reset when it ends.
    /// </exception>
    ValueTask<Stream> AcceptTunnelAsync(CancellationToken cancellationToken = default);
}
