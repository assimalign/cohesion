using System;

namespace Assimalign.Cohesion.Http.Connections;

/// <summary>
/// Configures the HTTP/2 abuse-mitigation limits an <see cref="HttpConnectionListener"/>
/// enforces on every HTTP/2 connection it accepts.
/// </summary>
/// <remarks>
/// <para>
/// HTTP/2's frame/flow-control machinery already bounds concurrency, frame size, and the
/// connection/stream flow-control windows, but those bounds do not defend against the known
/// HTTP/2 abuse classes — rapid stream reset (CVE-2023-44487), CONTINUATION flooding, oversized
/// decoded header lists, and SETTINGS/PING floods — each of which forces the server to perform
/// unbounded work for a trivially cheap client message. These limits close those vectors.
/// </para>
/// <para>
/// Every limit carries a conservative default modelled on Kestrel's <c>Http2Limits</c>, so a
/// deployment is protected without any explicit configuration; mutate the instance exposed by
/// <see cref="HttpServerLimits.Http2"/> to tune it. When a limit is exceeded the connection is
/// terminated with <c>GOAWAY</c> carrying <c>ENHANCE_YOUR_CALM</c> (RFC 9113 §7, error code
/// <c>0x0b</c>) — the same escalation Kestrel uses — so a well-behaved peer can retry its
/// in-flight streams on a fresh connection.
/// </para>
/// <para>
/// These limits are consumed entirely by the HTTP/2 frame machinery; there is no DI, logging, or
/// configuration dependency in this package (Lane A guardrail — config binding is a Web.Hosting
/// builder-time concern). HTTP/3 stream limits are governed by the QUIC transport and are out of
/// scope for this surface.
/// </para>
/// </remarks>
public sealed class Http2Limits
{
    /// <summary>Default <see cref="MaxStreamsPerConnection"/> (mirrors the advertised SETTINGS_MAX_CONCURRENT_STREAMS).</summary>
    public const int DefaultMaxStreamsPerConnection = 100;

    /// <summary>Default <see cref="MaxRequestHeaderListSize"/> — a 16 KB DoS guard advertised as SETTINGS_MAX_HEADER_LIST_SIZE.</summary>
    public const int DefaultMaxRequestHeaderListSize = 16 * 1024;

    /// <summary>Default <see cref="MaxResetStreamsPerWindow"/> for the rapid-reset sliding window.</summary>
    public const int DefaultMaxResetStreamsPerWindow = 200;

    /// <summary>Default <see cref="MaxSettingsFramesPerWindow"/> for the SETTINGS-flood sliding window.</summary>
    public const int DefaultMaxSettingsFramesPerWindow = 100;

    /// <summary>Default <see cref="MaxPingFramesPerWindow"/> for the PING-flood sliding window.</summary>
    public const int DefaultMaxPingFramesPerWindow = 100;

    private int _maxStreamsPerConnection = DefaultMaxStreamsPerConnection;
    private int _maxRequestHeaderListSize = DefaultMaxRequestHeaderListSize;
    private int _maxResetStreamsPerWindow = DefaultMaxResetStreamsPerWindow;
    private int _maxSettingsFramesPerWindow = DefaultMaxSettingsFramesPerWindow;
    private int _maxPingFramesPerWindow = DefaultMaxPingFramesPerWindow;
    private TimeSpan _floodDetectionWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the maximum number of concurrent streams the server will serve on a single
    /// HTTP/2 connection. Advertised to the peer as <c>SETTINGS_MAX_CONCURRENT_STREAMS</c>
    /// (RFC 9113 §5.1.2); a client that opens a new stream beyond the cap has that stream refused
    /// with <c>RST_STREAM(REFUSED_STREAM)</c> and can safely retry it on another connection.
    /// Defaults to <see cref="DefaultMaxStreamsPerConnection"/> (<c>100</c>).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is less than <c>1</c>.</exception>
    public int MaxStreamsPerConnection
    {
        get => _maxStreamsPerConnection;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxStreamsPerConnection = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum size, in octets, of a request's header list. This single bound is
    /// advertised to the peer as <c>SETTINGS_MAX_HEADER_LIST_SIZE</c> (RFC 9113 §6.5.2), caps the
    /// raw header-block bytes accumulated across a HEADERS frame and its CONTINUATION frames (the
    /// CONTINUATION-flood defence), and caps the decoded header list — the sum of
    /// <c>name-length + value-length + 32</c> across every field (RFC 9113 §10.5.1). A request
    /// exceeding it terminates the connection with <c>GOAWAY(ENHANCE_YOUR_CALM)</c>. Defaults to
    /// <see cref="DefaultMaxRequestHeaderListSize"/> (16 KB).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is less than <c>1</c>.</exception>
    public int MaxRequestHeaderListSize
    {
        get => _maxRequestHeaderListSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxRequestHeaderListSize = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of stream resets (a client opening a stream and then
    /// resetting it, or the server refusing it) permitted within any <see cref="FloodDetectionWindow"/>
    /// before the connection is judged to be exhibiting the rapid-reset abuse pattern
    /// (CVE-2023-44487) and terminated with <c>GOAWAY(ENHANCE_YOUR_CALM)</c>. Defaults to
    /// <see cref="DefaultMaxResetStreamsPerWindow"/> (<c>200</c>).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is less than <c>1</c>.</exception>
    public int MaxResetStreamsPerWindow
    {
        get => _maxResetStreamsPerWindow;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxResetStreamsPerWindow = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of inbound <c>SETTINGS</c> frames permitted within any
    /// <see cref="FloodDetectionWindow"/> before the connection is judged to be flooding and
    /// terminated with <c>GOAWAY(ENHANCE_YOUR_CALM)</c>. Defaults to
    /// <see cref="DefaultMaxSettingsFramesPerWindow"/> (<c>100</c>).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is less than <c>1</c>.</exception>
    public int MaxSettingsFramesPerWindow
    {
        get => _maxSettingsFramesPerWindow;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxSettingsFramesPerWindow = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of inbound <c>PING</c> frames permitted within any
    /// <see cref="FloodDetectionWindow"/> before the connection is judged to be flooding and
    /// terminated with <c>GOAWAY(ENHANCE_YOUR_CALM)</c>. Each inbound PING forces the server to
    /// emit a PING acknowledgement, so an unbounded PING stream is an amplification vector.
    /// Defaults to <see cref="DefaultMaxPingFramesPerWindow"/> (<c>100</c>).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is less than <c>1</c>.</exception>
    public int MaxPingFramesPerWindow
    {
        get => _maxPingFramesPerWindow;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxPingFramesPerWindow = value;
        }
    }

    /// <summary>
    /// Gets or sets the trailing time window over which <see cref="MaxResetStreamsPerWindow"/>,
    /// <see cref="MaxSettingsFramesPerWindow"/>, and <see cref="MaxPingFramesPerWindow"/> are
    /// evaluated. A longer window makes the flood detectors more sensitive (fewer events tolerated
    /// per unit time). Defaults to 5 seconds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is less than or equal to <see cref="TimeSpan.Zero"/>.</exception>
    public TimeSpan FloodDetectionWindow
    {
        get => _floodDetectionWindow;
        set
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The flood-detection window must be a positive duration.");
            }

            _floodDetectionWindow = value;
        }
    }
}
