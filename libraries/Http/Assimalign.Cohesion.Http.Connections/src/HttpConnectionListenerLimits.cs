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
/// Enforcement is per version: the HTTP/1.1 read/write path enforces the body-size cap, the
/// connection timeouts, and the request-body / response data rates today; HTTP/2 bounds
/// request-body buffering through flow-control backpressure (a hard body-size cap, the connection
/// timeouts, and the data rates are tracked follow-up work); HTTP/3's equivalents are delegated to
/// the QUIC transport. On every version <see cref="MaxRequestBodySize"/> additionally seeds each
/// request's parse context, so request-parse interceptors observe and adjust the same knob no
/// matter which protocol served the request. Each property documents where it is enforced so an
/// operator never has to guess.
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
    /// HTTP/1.1 read path today; HTTP/2 bounds body buffering via flow-control backpressure, with
    /// the hard cap tracked as follow-up work.
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
    /// Gets or sets how long an idle keep-alive connection is held open while waiting for the
    /// next request to begin before the transport reclaims it. Set to
    /// <see cref="Timeout.InfiniteTimeSpan"/> to disable the timeout. Defaults to 130 seconds.
    /// Enforced by the HTTP/1.1 connection loop today.
    /// </summary>
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
    /// once the first request byte has been received, before reclaiming the connection. This is
    /// the primary Slowloris defence. Set to <see cref="Timeout.InfiniteTimeSpan"/> to disable the
    /// timeout. Defaults to 30 seconds. Enforced by the HTTP/1.1 connection loop today.
    /// </summary>
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
    /// fails with <c>408 Request Timeout</c> (RFC 9110 §15.5.9) semantics. Defaults to 240 octets
    /// per second over a 5-second grace period (Kestrel's <c>MinRequestBodyDataRate</c> parity).
    /// Enforced by the HTTP/1.1 streaming request-body read today; HTTP/2 paces the body through
    /// flow-control backpressure and HTTP/3 through QUIC flow control, so this rate is a
    /// tracked follow-up on those versions.
    /// </summary>
    /// <remarks>
    /// The rate is an <em>average</em> measured only over time the transport actually spent waiting
    /// for the peer, so a slow application consuming a healthy body never trips it; see
    /// <see cref="HttpMinDataRate"/>.
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
    /// parity). Enforced by the HTTP/1.1 streaming response write path today (the incremental
    /// chunked sink); the buffered response path and the HTTP/2 / HTTP/3 send paths do not enforce
    /// it yet.
    /// </summary>
    /// <remarks>
    /// As with <see cref="MinRequestBodyDataRate"/>, the rate is an average measured only over time
    /// spent waiting on the peer to accept bytes; see <see cref="HttpMinDataRate"/>.
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
