using System;

namespace Assimalign.Cohesion.Http.Connections;

/// <summary>
/// Configuration for the HTTP/3 (QUIC) listener registered through
/// <see cref="HttpConnectionListenerOptions.UseHttp3(System.Func{Assimalign.Cohesion.Connections.IMultiplexedConnectionListener}, System.Action{Http3ConnectionListenerOptions})"/>.
/// HTTP/3-specific tunables live here — rather than on the shared
/// <see cref="HttpConnectionListenerOptions"/> — so each protocol version owns
/// its own configuration surface.
/// </summary>
public sealed class Http3ConnectionListenerOptions
{
    /// <summary>
    /// Gets the QPACK field-compression configuration (RFC 9204). The default is
    /// the static-only profile (the dynamic table is disabled).
    /// </summary>
    public Http3QPackOptions QPack { get; } = new();

    /// <summary>
    /// Gets the shared request-shaping limits applied to every connection this registration
    /// accepts. The bounds carry the same conservative defaults as the other protocol versions;
    /// mutate the returned instance to tune them.
    /// </summary>
    public Http3Limits Limits { get; } = new Http3Limits();

    /// <summary>
    /// The HTTP/3 limits: the shared <see cref="HttpConnectionListenerLimits"/> plus the bound on a
    /// request stream's HEADERS frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// HTTP/3's frame-rate and concurrency bounds are governed by the QUIC transport (stream
    /// limits, connection flow control). The one frame the server must buffer whole is a request's
    /// HEADERS frame — QPACK decodes a complete field section — so its size is capped here by
    /// <see cref="MaxRequestHeadersFrameSize"/>; every other request-stream frame is streamed or
    /// skipped incrementally.
    /// </para>
    /// <para>
    /// Of the inherited shared limits, <see cref="HttpConnectionListenerLimits.MaxRequestBodySize"/>
    /// is enforced by the HTTP/3 request-body stream: the request is dispatched at its HEADERS frame,
    /// the per-request value (seeded into each request's parse context, where request-parse
    /// interceptors and the <c>Assimalign.Cohesion.Http.RequestLimits</c> feature can adjust it) freezes
    /// at the first body read, and a body that exceeds it is answered with <c>413 Content Too Large</c>.
    /// The connection timeouts and data rates are tracked follow-up work.
    /// </para>
    /// </remarks>
    public sealed class Http3Limits : HttpConnectionListenerLimits
    {
        /// <summary>
        /// Default <see cref="MaxRequestHeadersFrameSize"/>: 32 KB, the HTTP/1.1 default for the
        /// whole request header section.
        /// </summary>
        public const int DefaultMaxRequestHeadersFrameSize = 32 * 1024;

        private int _maxRequestHeadersFrameSize = DefaultMaxRequestHeadersFrameSize;

        /// <summary>
        /// Gets or sets the maximum payload size, in octets, of a HEADERS frame on a request stream —
        /// the QPACK-encoded field section of the request head, or of its trailer section. The frame
        /// length is checked when the frame header arrives, before any of its payload is buffered; a
        /// longer frame resets that request stream with <c>H3_FRAME_ERROR</c> (RFC 9114 §7.1) while the
        /// connection's other streams keep being served. Defaults to
        /// <see cref="DefaultMaxRequestHeadersFrameSize"/> (32 KB).
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is less than <c>1</c>.</exception>
        public int MaxRequestHeadersFrameSize
        {
            get => _maxRequestHeadersFrameSize;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
                _maxRequestHeadersFrameSize = value;
            }
        }
    }
}
