using System;
using System.Net.WebSockets;
using System.Threading;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The settings for one accepted WebSocket: the subprotocol to select, keep-alive, and the
/// permessage-deflate compression offer to accept.
/// </summary>
/// <remarks>
/// <para>
/// A <see langword="null"/> keep-alive or compression setting takes the exchange's default. A
/// policy layer supplies its own defaults by decorating <see cref="IHttpWebSocketFeature"/>, as the
/// Web pipeline's <c>UseWebSockets</c> does; without one, the defaults are
/// <see cref="WebSocket.DefaultKeepAliveInterval"/>, no keep-alive timeout, and no compression.
/// </para>
/// <para>
/// The options are read when <see cref="IHttpWebSocketFeature.AcceptWebSocketAsync"/> runs and are
/// not retained, so one instance can serve many accepts.
/// </para>
/// </remarks>
public sealed class HttpWebSocketAcceptOptions
{
    private TimeSpan? _keepAliveInterval;
    private TimeSpan? _keepAliveTimeout;
    private int _serverMaxWindowBits = 15;

    /// <summary>
    /// Gets or sets the subprotocol to select, sent back in <c>Sec-WebSocket-Protocol</c>. It must
    /// be one of <see cref="IHttpWebSocketFeature.RequestedProtocols"/>, compared case-sensitively;
    /// <see langword="null"/> selects none.
    /// </summary>
    public string? SubProtocol { get; set; }

    /// <summary>
    /// Gets or sets how often the server sends a keep-alive frame on an otherwise idle socket.
    /// <see cref="TimeSpan.Zero"/> or <see cref="Timeout.InfiniteTimeSpan"/> disables keep-alive;
    /// <see langword="null"/> takes the exchange's default.
    /// </summary>
    /// <remarks>
    /// Keep-alive keeps proxies and load balancers from closing an idle connection. Without a
    /// <see cref="KeepAliveTimeout"/>, the keep-alive frame is an unsolicited <c>Pong</c>, which the
    /// peer does not answer.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is negative and is not <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    public TimeSpan? KeepAliveInterval
    {
        get => _keepAliveInterval;
        set => _keepAliveInterval = ValidateInterval(value, nameof(KeepAliveInterval));
    }

    /// <summary>
    /// Gets or sets how long the server waits for the <c>Pong</c> that answers a keep-alive
    /// <c>Ping</c> before it aborts the socket. <see cref="TimeSpan.Zero"/> or
    /// <see cref="Timeout.InfiniteTimeSpan"/> sends unsolicited <c>Pong</c> frames instead and never
    /// times out; <see langword="null"/> takes the exchange's default.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is negative and is not <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    public TimeSpan? KeepAliveTimeout
    {
        get => _keepAliveTimeout;
        set => _keepAliveTimeout = ValidateInterval(value, nameof(KeepAliveTimeout));
    }

    /// <summary>
    /// Gets or sets whether the server accepts a permessage-deflate offer (RFC 7692) from the
    /// client. <see langword="null"/> takes the exchange's default, which is off unless a policy
    /// enabled it.
    /// </summary>
    /// <remarks>
    /// Compression is off by default because it opens side channels: when a message compresses
    /// data an attacker controls together with a secret, the compressed size leaks the secret
    /// (the CRIME and BREACH attacks). Enable it only for sockets whose messages never mix the two.
    /// The client must also offer the extension; when it does not, the socket is not compressed.
    /// </remarks>
    public bool? DangerousEnableCompression { get; set; }

    /// <summary>
    /// Gets or sets whether the server resets its compression context after every message
    /// (<c>server_no_context_takeover</c>), trading compression ratio for memory. Applies only when
    /// compression is negotiated; the server also resets when the client asks it to.
    /// </summary>
    public bool DisableServerContextTakeover { get; set; }

    /// <summary>
    /// Gets or sets the base-2 logarithm of the largest LZ77 window the server compresses with
    /// (<c>server_max_window_bits</c>), from 9 to 15. The default is 15. A client that asks for a
    /// smaller window gets the smaller of the two. Applies only when compression is negotiated.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 9 or greater than 15.</exception>
    public int ServerMaxWindowBits
    {
        get => _serverMaxWindowBits;
        set
        {
            // RFC 7692 allows 8, but the BCL's zlib-based deflater does not (WebSocketDeflateOptions
            // accepts 9 to 15).
            if (value is < 9 or > 15)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ServerMaxWindowBits),
                    value,
                    "The server's maximum window size must be from 9 to 15 bits.");
            }

            _serverMaxWindowBits = value;
        }
    }

    private static TimeSpan? ValidateInterval(TimeSpan? value, string propertyName)
    {
        if (value is TimeSpan interval && interval < TimeSpan.Zero && interval != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                propertyName,
                interval,
                "The interval must be zero or positive, or Timeout.InfiniteTimeSpan.");
        }

        return value;
    }
}
