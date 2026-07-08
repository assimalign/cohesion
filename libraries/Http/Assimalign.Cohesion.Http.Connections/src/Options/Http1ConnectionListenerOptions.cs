using System;

namespace Assimalign.Cohesion.Http.Connections;

/// <summary>
/// Configuration for an HTTP/1.1 listener registered through
/// <see cref="HttpConnectionListenerOptions.UseHttp1(Assimalign.Cohesion.Connections.IConnectionListener, Action{Http1ConnectionListenerOptions})"/>.
/// HTTP/1.1-specific tunables live here — rather than on the shared
/// <see cref="HttpConnectionListenerOptions"/> — so each protocol version owns its own
/// configuration surface, captured per registration.
/// </summary>
public sealed class Http1ConnectionListenerOptions
{
    /// <summary>
    /// Gets the HTTP/1.1 request-shaping and timeout limits enforced on every connection this
    /// registration accepts. The bounds carry conservative Kestrel-parity defaults, so a listener
    /// is protected against oversized request lines / header sections, oversized request bodies,
    /// and idle / slow-header (Slowloris) peers without any explicit configuration. Mutate the
    /// returned instance to tune them.
    /// </summary>
    public Http1Limits Limits { get; } = new Http1Limits();

    /// <summary>
    /// The HTTP/1.1 limits: extends the shared <see cref="HttpConnectionListenerLimits"/> with
    /// the bounds specific to the HTTP/1.1 wire format (the request line and the header section).
    /// </summary>
    /// <remarks>
    /// These limits are the transport's first line of defence against HTTP/1.1
    /// resource-exhaustion abuse: an unbounded request line or header section is a live
    /// memory-exhaustion vector, and the inherited timeouts close the Slowloris vector.
    /// Enforcement lives in the HTTP/1.1 read path (<c>Http1MessageReader</c> /
    /// <c>Http1MessageBodyReader</c>) and connection loop; violations are answered with
    /// <c>414</c> / <c>431</c> / <c>413</c> before the connection is closed.
    /// </remarks>
    public sealed class Http1Limits : HttpConnectionListenerLimits
    {
        private int _maxRequestLineSize = 8 * 1024;
        private int _maxRequestHeaderCount = 100;
        private int _maxRequestHeadersTotalSize = 32 * 1024;

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
        /// Gets or sets the maximum number of header fields permitted in a single request. A
        /// request carrying more header fields than this is rejected with
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
        /// Gets or sets the maximum combined size, in octets, of the request header section
        /// (every header field line, including its terminating CRLF). A header section exceeding
        /// this bound is rejected with <c>431 Request Header Fields Too Large</c>
        /// (RFC 9110 §15.5.22) before the connection is closed. Defaults to <c>32768</c> (32 KB).
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
    }
}
