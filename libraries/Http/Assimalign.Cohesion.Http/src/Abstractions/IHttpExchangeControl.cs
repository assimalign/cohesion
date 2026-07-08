using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The transport's per-exchange control surface, surfaced through
/// <see cref="HttpResponseInterceptorContext.Control"/>. It is the <b>single generic seam</b> by
/// which interceptor hooks and feature packages tap the connection's handling of the current
/// exchange: reading and directing its control flow (<see cref="HttpExchangeDirective"/> —
/// continue, abort, or give up control) and performing the transport-owned wire actions that must
/// happen outside the normal buffered-response path (interim <c>1xx</c> writes, raw-stream
/// takeover).
/// </summary>
/// <remarks>
/// <para>
/// One generic control surface deliberately replaces per-capability contracts (the former
/// <c>IHttpConnectionTakeover</c> and <c>IHttpInterimResponseWriter</c>): feature packages —
/// protocol upgrade, interim responses, and future WebSockets / compression / timeout features —
/// compose from this contract plus the <see cref="IHttpRequestInterceptor"/> /
/// <see cref="IHttpResponseInterceptor"/> lifecycle hooks, so tapping a new lifecycle point never
/// requires a new core abstraction or new transport plumbing. The transport implements this
/// per protocol version and owns every wire encoding behind it; feature packages reference only
/// the protocol core.
/// </para>
/// <para>
/// Capability probes (<see cref="CanWriteInterimResponse"/>, <see cref="CanTakeOver"/>) are the
/// report-don't-throw discovery path: a caller checks them and learns the exchange state without
/// provoking an exception. The imperative members throw only on genuine misuse (taking over an
/// exchange that cannot be taken over, writing an interim response after the final response
/// started).
/// </para>
/// <para>
/// The control is exchange-scoped and is not thread-safe; it must be driven from the exchange's
/// handling flow (interceptor hooks, the application handler, features it installed).
/// </para>
/// </remarks>
public interface IHttpExchangeControl
{
    /// <summary>
    /// Gets the exchange's current control-flow directive: <see cref="HttpExchangeDirective.Continue"/>
    /// until <see cref="Abort"/> or <see cref="TakeOver"/> transitions it. The transport reads this
    /// at its lifecycle checkpoints to decide whether to keep driving the exchange, reject it on
    /// the wire, or suppress its own response entirely.
    /// </summary>
    HttpExchangeDirective Directive { get; }

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
    /// RFC 9114 §4.2) and bootstrap other protocols via extended CONNECT — so takeover reports
    /// <see langword="false"/> there. Also <see langword="false"/> once the exchange has already
    /// been taken over or the final response has started.
    /// </summary>
    bool CanTakeOver { get; }

    /// <summary>
    /// Takes over the exchange's connection — the transport gives up control: it suppresses its
    /// own response for the exchange (the normal send becomes a no-op, so nothing is
    /// double-written onto what is now a raw byte stream), stops reusing the connection for
    /// further HTTP requests, and transitions <see cref="Directive"/> to
    /// <see cref="HttpExchangeDirective.TakeOver"/>. This is the escape hatch RFC 9110 §7.8
    /// protocol upgrades (<c>101 Switching Protocols</c>) and §9.3.6 <c>CONNECT</c> tunnels need.
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
    /// over (the capability is one-shot), or the final response has started.
    /// </exception>
    Stream TakeOver();

    /// <summary>
    /// Aborts the exchange at the protocol layer, transitioning <see cref="Directive"/> to
    /// <see cref="HttpExchangeDirective.Abort"/>. The transport rejects the exchange with the wire
    /// behavior appropriate to its version: HTTP/2 resets the stream (<c>RST_STREAM</c>) and
    /// HTTP/3 resets the request stream — each leaving the multiplexed connection's other streams
    /// intact — while HTTP/1.1, which has no per-exchange reset finer than the connection, writes
    /// no response and ends the connection after the exchange. The exchange's cancellation token
    /// is tripped so in-flight handler work observes the abort.
    /// </summary>
    void Abort();
}
