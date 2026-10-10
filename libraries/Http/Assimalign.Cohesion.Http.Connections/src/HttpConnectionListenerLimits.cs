using System;
using System.Threading;

namespace Assimalign.Cohesion.Http.Connections;

/// <summary>
/// The request-shaping and timeout limits shared by every HTTP protocol version. Each
/// version-specific options surface (<see cref="Http1ConnectionListenerOptions"/>,
/// <see cref="Http2ConnectionListenerOptions"/>, <see cref="Http3ConnectionListenerOptions"/>)
/// exposes a derived limits type (<see cref="Http1Limits"/>, <see cref="Http2Limits"/>,
/// <see cref="Http3Limits"/>) that adds the bounds specific to that version's wire format.
/// </summary>
/// <remarks>
/// <para>
/// Only limits that are meaningful for HTTP/1.1, HTTP/2, and HTTP/3 alike live here: every
/// version carries request bodies, holds idle connections, has a header-arrival phase, and can be
/// stalled by a slow peer. Wire-format-specific bounds (the HTTP/1.1 request line and header
/// section, the HTTP/2 frame-rate caps) belong on the derived types. Every limit has a
/// conservative default modelled on Kestrel's <c>KestrelServerLimits</c> so a deployment is
/// protected without explicit configuration.
/// </para>
/// <para>
/// Enforcement is per version. Every version enforces the body-size cap, the keep-alive and
/// request-headers timeouts, and the minimum request-body data rate: HTTP/1.1 in its read path and
/// connection loop, HTTP/2 in its frame pump and request-body pipe, and HTTP/3 in its accept loop,
/// request-head read and lazily read request body. The minimum response data rate is enforced by the
/// HTTP/1.1 streaming response sink only; HTTP/2 and HTTP/3 responses are paced by flow control. On
/// every version <see cref="MaxRequestBodySize"/> additionally seeds each request's parse context, so
/// request-parse interceptors observe and adjust the same knob no matter which protocol served the
/// request. Each property documents where, and with what signal, it is enforced so an operator never
/// has to guess.
/// </para>
/// </remarks>
public abstract class HttpConnectionListenerLimits
{
    private long? _maxRequestBodySize = 30_000_000;
    private TimeSpan _keepAliveTimeout = TimeSpan.FromSeconds(130);
    private TimeSpan _requestHeadersTimeout = TimeSpan.FromSeconds(30);
    private HttpMinDataRate? _minRequestBodyDataRate = new HttpMinDataRate(bytesPerSecond: 240, gracePeriod: TimeSpan.FromSeconds(5));
    private HttpMinDataRate? _minResponseDataRate = new HttpMinDataRate(bytesPerSecond: 240, gracePeriod: TimeSpan.FromSeconds(5));

    /// <summary>
    /// Gets or sets the maximum allowed request body size, in octets, or <see langword="null"/>
    /// to leave the body size unbounded. A request whose <c>Content-Length</c> declaration (or
    /// accumulated chunked body) exceeds this bound is rejected with <c>413 Content Too Large</c>
    /// (RFC 9110 §15.5.14). Defaults to <c>30000000</c> (~28.6 MB). This is the connection-wide
    /// default, seeded into each request's parse context; a registered
    /// <see cref="Assimalign.Cohesion.Http.IHttpExchangeInterceptor"/> may raise or lower the cap
    /// per request before the body is read (the <c>Assimalign.Cohesion.Http.RequestLimits</c>
    /// package surfaces it as a typed <c>IHttpMaxRequestBodySizeFeature</c>). Enforced by the
    /// HTTP/1.1 and HTTP/3 request-body reads, where the per-request value freezes at the first body
    /// read (both answer <c>413</c> while the response head is uncommitted; HTTP/1.1 then closes
    /// the connection, and HTTP/3 resets a stream whose streamed response had already started with
    /// <c>H3_REQUEST_CANCELLED</c>), and by HTTP/2, which
    /// freezes the value when the request is dispatched, rejects a larger declared
    /// <c>content-length</c> before reading the body, and answers a body that grows past it on
    /// receipt with <c>413</c> (a stream reset when the response has already started).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is negative.</exception>
    public long? MaxRequestBodySize
    {
        get => _maxRequestBodySize;
        set
        {
            if (value is < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The maximum request body size must be non-negative or null (unbounded).");
            }

            _maxRequestBodySize = value;
        }
    }

    /// <summary>
    /// Gets or sets how long an idle connection is held open while waiting for the next request to
    /// begin before the transport reclaims it. Set to <see cref="Timeout.InfiniteTimeSpan"/> to disable
    /// the timeout. Defaults to 130 seconds.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><b>HTTP/1.1</b> — the connection loop waits for the first octet of each request,
    /// the first included, under this deadline, and closes a connection that sends none without a
    /// response.</description></item>
    /// <item><description><b>HTTP/2</b> — the connection is idle while it carries no stream, and the
    /// deadline runs from its acceptance (the connection preface and the client's SETTINGS arrive under
    /// it) or from the end of its last exchange, whichever is later. Frames such as PING do not move the
    /// deadline, and neither does a stream that never became an exchange (refused, reset as malformed,
    /// or rejected by a request-parse interceptor). An idle connection is closed with
    /// <c>GOAWAY(NO_ERROR)</c> (RFC 9113 §9.1); one that never sent its preface is closed without a
    /// frame.</description></item>
    /// <item><description><b>HTTP/3</b> — the connection is idle while no request stream is in flight, and
    /// the deadline runs from the start of its receive loop or from the end of its last exchange,
    /// whichever is later; a request stream whose head yields no exchange does not move it. An idle
    /// connection is closed gracefully: <c>GOAWAY</c>, then the QUIC connection closes with
    /// <c>H3_NO_ERROR</c> (RFC 9114 §5.2). QUIC's own idle timeout, which any packet resets, still
    /// applies beneath it.</description></item>
    /// </list>
    /// Either way the connection's receive enumeration ends, so a host releases its connection
    /// slot.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the assigned value is not <see cref="Timeout.InfiniteTimeSpan"/> and is less than
    /// or equal to <see cref="TimeSpan.Zero"/>.
    /// </exception>
    public TimeSpan KeepAliveTimeout
    {
        get => _keepAliveTimeout;
        set
        {
            ValidateTimeout(value);
            _keepAliveTimeout = value;
        }
    }

    /// <summary>
    /// Gets or sets how long the transport waits for a request's header section to arrive in full
    /// once the first request byte has been received. This is the primary Slowloris defence. Set to
    /// <see cref="Timeout.InfiniteTimeSpan"/> to disable the timeout. Defaults to 30 seconds.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><b>HTTP/1.1</b> — from the first octet of the request line to the end of the
    /// header section; the request is answered <c>408 Request Timeout</c> and the connection
    /// closed.</description></item>
    /// <item><description><b>HTTP/2</b> — from the header of a HEADERS frame to END_HEADERS, for a
    /// request head and a trailer section alike. A field block holds the whole connection until it ends
    /// (RFC 9113 §6.10), so the connection is closed with <c>GOAWAY(ENHANCE_YOUR_CALM)</c>
    /// (RFC 9113 §10.5); requests already received in full stay answerable.</description></item>
    /// <item><description><b>HTTP/3</b> — from a request stream's acceptance until its HEADERS frame has
    /// arrived and decoded; the stream alone is reset with <c>H3_REQUEST_REJECTED</c> (RFC 9114
    /// §4.1.1), so the client may retry it, and the connection keeps serving.</description></item>
    /// </list>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the assigned value is not <see cref="Timeout.InfiniteTimeSpan"/> and is less than
    /// or equal to <see cref="TimeSpan.Zero"/>.
    /// </exception>
    public TimeSpan RequestHeadersTimeout
    {
        get => _requestHeadersTimeout;
        set
        {
            ValidateTimeout(value);
            _requestHeadersTimeout = value;
        }
    }

    /// <summary>
    /// Gets or sets the minimum rate, in octets per second (with a grace period), at which the
    /// request body must be received once the grace period elapses, or <see langword="null"/> to
    /// disable the check. A peer that trickles its body below this rate is reclaimed: the read
    /// fails, and the exchange is answered <c>408 Request Timeout</c> (RFC 9110 §15.5.9) when its
    /// response has not started. Defaults to 240 octets per second over a 5-second grace period
    /// (Kestrel's <c>MinRequestBodyDataRate</c> parity).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rate is an <em>average</em> measured from the first read, only over time the transport
    /// actually spent waiting for the peer, so a slow application consuming a healthy body never trips
    /// it; see <see cref="HttpMinDataRate"/>. A CONNECT tunnel's octets are not a request body and are
    /// never held to it.
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>HTTP/1.1</b> — the streaming request-body read; the connection closes after
    /// the exchange.</description></item>
    /// <item><description><b>HTTP/2</b> — the stream's request-body read. The part of a wait during which
    /// the connection-level receive window cannot carry a full frame is not charged, since other streams
    /// may then be what holds the peer back, but each body is excused for at most the grace period in
    /// total, so a peer cannot switch the rate off by pinning the window with one unread stream. Before
    /// the response starts the transport answers <c>408</c> and resets the stream with
    /// <c>NO_ERROR</c> (RFC 9113 §8.1); after, it resets it with <c>CANCEL</c>. The connection keeps
    /// serving its other streams.</description></item>
    /// <item><description><b>HTTP/3</b> — the lazily read request body. The request stream is stopped
    /// with <c>STOP_SENDING(H3_NO_ERROR)</c> (RFC 9114 §4.1) and the exchange answered <c>408</c> when
    /// its response head is uncommitted, or the stream reset with <c>H3_REQUEST_CANCELLED</c> when a
    /// streamed response head is already on the wire.</description></item>
    /// </list>
    /// <para>
    /// The limit is listener-wide, with no per-request override. On HTTP/2 and HTTP/3 it applies from
    /// this release on; earlier releases did not enforce it there. A request body that legitimately
    /// idles — a client-streaming or duplex call, a long-lived upload that sends a message every few
    /// seconds — fails once an idle gap outlasts what is left of its allowance, about the grace period
    /// plus the octets received so far over the rate (5 seconds plus 1 second per 240 octets by
    /// default). An endpoint that serves such bodies needs a longer grace period or a lower rate, or
    /// <see langword="null"/>, each of which also weakens or removes the slow-body defence for every
    /// other request on the endpoint.
    /// </para>
    /// </remarks>
    public HttpMinDataRate? MinRequestBodyDataRate
    {
        get => _minRequestBodyDataRate;
        set => _minRequestBodyDataRate = value;
    }

    /// <summary>
    /// Gets or sets the minimum rate, in octets per second (with a grace period), at which the
    /// response body must be written to the peer once the grace period elapses, or
    /// <see langword="null"/> to disable the check. A reader that fails to drain the response below
    /// this rate stops blocking the server: the write fails and the exchange is aborted. Defaults to
    /// 240 octets per second over a 5-second grace period (Kestrel's <c>MinResponseDataRate</c>
    /// parity). Enforced by the HTTP/1.1 streaming response write path (the incremental chunked
    /// sink) only; the HTTP/1.1 buffered response path and the HTTP/2 and HTTP/3 send paths, which
    /// flow control paces, do not enforce it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// As with <see cref="MinRequestBodyDataRate"/>, the rate is an average measured only over time
    /// spent waiting on the peer to accept bytes; see <see cref="HttpMinDataRate"/>.
    /// </para>
    /// <para>
    /// On HTTP/2 and HTTP/3 a peer that grants no flow-control credit (an HTTP/2
    /// <c>SETTINGS_INITIAL_WINDOW_SIZE</c> of 0, or no QUIC stream credit) parks the response writer for
    /// as long as it likes. The exchange never ends, so the connection stays busy: the
    /// <see cref="KeepAliveTimeout"/> never arms, and one request to an endpoint with a non-empty
    /// response holds the connection indefinitely.
    /// </para>
    /// </remarks>
    public HttpMinDataRate? MinResponseDataRate
    {
        get => _minResponseDataRate;
        set => _minResponseDataRate = value;
    }

    private static void ValidateTimeout(TimeSpan value)
    {
        if (value == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "The timeout must be positive or Timeout.InfiniteTimeSpan.");
        }
    }
}
