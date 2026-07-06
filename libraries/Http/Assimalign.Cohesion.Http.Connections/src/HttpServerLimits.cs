using System;
using System.Threading;

namespace Assimalign.Cohesion.Http.Connections;

/// <summary>
/// Configures the request-shaping and timeout limits an <see cref="HttpConnectionListener"/>
/// enforces on the connections it accepts.
/// </summary>
/// <remarks>
/// <para>
/// These limits are the transport's first line of defence against resource-exhaustion abuse:
/// an unbounded request line or header section is a live memory-exhaustion vector, and a peer
/// that opens a connection and then dribbles (or never sends) request bytes is a Slowloris
/// vector. Every limit here has a conservative default modelled on Kestrel's
/// <c>KestrelServerLimits</c> so a deployment is protected without any explicit configuration.
/// </para>
/// <para>
/// The size limits are enforced by the HTTP/1.1 read path
/// (<c>Http1MessageReader</c> / <c>Http1MessageBodyReader</c>); the timeouts are enforced by the
/// HTTP/1.1 connection loop. HTTP/2 and HTTP/3 abuse limits are governed separately by their
/// own frame/flow-control machinery and are out of scope for this surface.
/// </para>
/// </remarks>
public sealed class HttpServerLimits
{
    private int _maxRequestLineSize = 8 * 1024;
    private int _maxRequestHeaderCount = 100;
    private int _maxRequestHeadersTotalSize = 32 * 1024;
    private long? _maxRequestBodySize = 30_000_000;
    private TimeSpan _keepAliveTimeout = TimeSpan.FromSeconds(130);
    private TimeSpan _requestHeadersTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the maximum allowed size, in octets, of the HTTP/1.1 request line
    /// (method, request-target, and version). A request line exceeding this bound is rejected
    /// with <c>414 URI Too Long</c> (RFC 9110 §15.5.15) before the connection is closed.
    /// Defaults to <c>8192</c> (8 KB).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is less than <c>1</c>.</exception>
    public int MaxRequestLineSize
    {
        get => _maxRequestLineSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxRequestLineSize = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of header fields permitted in a single request. A request
    /// carrying more header fields than this is rejected with
    /// <c>431 Request Header Fields Too Large</c> (RFC 9110 §15.5.22) before the connection is
    /// closed. Defaults to <c>100</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is less than <c>1</c>.</exception>
    public int MaxRequestHeaderCount
    {
        get => _maxRequestHeaderCount;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxRequestHeaderCount = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum combined size, in octets, of the request header section (every
    /// header field line, including its terminating CRLF). A header section exceeding this bound
    /// is rejected with <c>431 Request Header Fields Too Large</c> (RFC 9110 §15.5.22) before the
    /// connection is closed. Defaults to <c>32768</c> (32 KB).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is less than <c>1</c>.</exception>
    public int MaxRequestHeadersTotalSize
    {
        get => _maxRequestHeadersTotalSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxRequestHeadersTotalSize = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum allowed request body size, in octets, or <see langword="null"/>
    /// to leave the body size unbounded. A request whose <c>Content-Length</c> declaration (or
    /// accumulated chunked body) exceeds this bound is rejected with <c>413 Content Too Large</c>
    /// (RFC 9110 §15.5.14). Defaults to <c>30000000</c> (~28.6 MB). This is the connection-wide
    /// default, seeded into each request's parse context; a registered
    /// <see cref="Assimalign.Cohesion.Http.IHttpRequestInterceptor"/> may raise or lower the cap
    /// per request before the body is read (the <c>Assimalign.Cohesion.Http.RequestLimits</c>
    /// package surfaces it as a typed <c>IHttpMaxRequestBodySizeFeature</c>).
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
    /// Gets or sets how long the transport waits for a request's header section (request line and
    /// all header fields) to arrive in full once the first request byte has been received, before
    /// reclaiming the connection. This is the primary Slowloris defence. Set to
    /// <see cref="Timeout.InfiniteTimeSpan"/> to disable the timeout. Defaults to 30 seconds.
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
