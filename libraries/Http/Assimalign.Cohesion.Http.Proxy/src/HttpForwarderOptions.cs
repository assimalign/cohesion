using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;

using Assimalign.Cohesion.Http.Internal;

using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Configures an <see cref="HttpForwarder"/>.
/// </summary>
/// <remarks>
/// <see cref="HttpForwarder.Create(HttpMessageInvoker, HttpForwarderOptions?)"/> copies the values,
/// so changing an options instance afterwards does not change a forwarder created from it.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("HttpForwarderOptions")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class HttpForwarderOptions
{
    /// <summary>
    /// The longest <see cref="ActivityTimeout"/> a timer can schedule, about 49.7 days.
    /// </summary>
    private static readonly TimeSpan _maximumTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1.0);

    private HttpForwardedHeaderAction _forwardedHeaderAction = HttpForwardedHeaderAction.Set;
    private HttpForwardedHeaderFormat _forwardedHeaderFormat = HttpForwardedHeaderFormat.XForwarded;
    private string? _viaPseudonym = CreatePseudonym();
    private TimeSpan _activityTimeout = TimeSpan.FromSeconds(100);
    private Version _version = NetHttpVersion.Version20;
    private HttpVersionPolicy _versionPolicy = HttpVersionPolicy.RequestVersionOrLower;
    private long _maxBufferedResponseBodySize = 4 * 1024 * 1024;

    /// <summary>
    /// Gets or sets what the forwarder does with the client's forwarded headers. The default is
    /// <see cref="HttpForwardedHeaderAction.Set"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined action.</exception>
    public HttpForwardedHeaderAction ForwardedHeaderAction
    {
        get => _forwardedHeaderAction;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _forwardedHeaderAction = value;
        }
    }

    /// <summary>
    /// Gets or sets the header family the forwarder writes when it adds a hop. The default is
    /// <see cref="HttpForwardedHeaderFormat.XForwarded"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined format.</exception>
    public HttpForwardedHeaderFormat ForwardedHeaderFormat
    {
        get => _forwardedHeaderFormat;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _forwardedHeaderFormat = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the forwarded request keeps the client's <c>Host</c>.
    /// The default is <see langword="false"/>: the request names the destination's authority, which
    /// is what a destination that serves several names, or a TLS certificate check, expects.
    /// </summary>
    public bool PreserveHost { get; set; }

    /// <summary>
    /// Gets or sets the pseudonym this forwarder adds to the <c>Via</c> header (RFC 9110 §7.6.3), or
    /// <see langword="null"/> to add no <c>Via</c> header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default is <c>cohesion-</c> followed by eight random hexadecimal digits, chosen per options
    /// instance, so two unrelated proxies never share one by accident. A request whose <c>Via</c>
    /// already names the pseudonym has passed through this forwarder before, and is answered with 508
    /// Loop Detected instead of being forwarded again. Give every instance of one deployment the same
    /// pseudonym to detect a loop between them.
    /// </para>
    /// <para>
    /// A pseudonym never names the host it runs on, so the header does not disclose it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The value is not an HTTP token.</exception>
    public string? ViaPseudonym
    {
        get => _viaPseudonym;
        set
        {
            if (value is not null && !HttpProxyFields.IsToken(value))
            {
                throw new ArgumentException(ErrorMessages.ViaPseudonymInvalid, nameof(value));
            }

            _viaPseudonym = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the forwarder also adds its <c>Via</c> entry to the
    /// responses it relays. The default is <see langword="false"/>: a reverse proxy usually keeps its
    /// presence from clients, while a forward proxy is expected to announce itself.
    /// </summary>
    public bool AddViaToResponses { get; set; }

    /// <summary>
    /// Gets or sets how long the forwarder waits for any progress from the destination or the client
    /// before it gives up: connecting, sending the request, waiting for the response head, each read
    /// of the response body, and each read or write on an upgraded connection. Every completed read
    /// or write restarts the wait. The default is 100 seconds;
    /// <see cref="Timeout.InfiniteTimeSpan"/> disables it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is not positive, is longer than about 49.7 days, and is not
    /// <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    public TimeSpan ActivityTimeout
    {
        get => _activityTimeout;
        set
        {
            if (value != Timeout.InfiniteTimeSpan && (value <= TimeSpan.Zero || value > _maximumTimeout))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _activityTimeout = value;
        }
    }

    /// <summary>
    /// Gets or sets the HTTP version the forwarder requests from the destination. The default is
    /// HTTP/2, which <see cref="VersionPolicy"/> lets fall back to HTTP/1.1. Protocol upgrades always
    /// use HTTP/1.1.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public Version Version
    {
        get => _version;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _version = value;
        }
    }

    /// <summary>
    /// Gets or sets how the forwarder may depart from <see cref="Version"/>. The default is
    /// <see cref="HttpVersionPolicy.RequestVersionOrLower"/>: HTTP/2 where TLS negotiates it, and
    /// HTTP/1.1 otherwise, including every cleartext destination.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined policy.</exception>
    public HttpVersionPolicy VersionPolicy
    {
        get => _versionPolicy;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _versionPolicy = value;
        }
    }

    /// <summary>
    /// Gets or sets the largest response body, in bytes, the forwarder holds in memory when the
    /// application cannot stream responses. The default is 4 MiB.
    /// </summary>
    /// <remarks>
    /// A response is relayed as it arrives when the exchange carries
    /// <see cref="IHttpResponseStreamingFeature"/> (the host registers
    /// <see cref="HttpResponseStreamingInterceptor"/>). Without it, the whole body is read before any
    /// of it is sent, and a larger body is answered with 502 Bad Gateway instead.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public long MaxBufferedResponseBodySize
    {
        get => _maxBufferedResponseBodySize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxBufferedResponseBodySize = value;
        }
    }

    private static string CreatePseudonym()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return "cohesion-" + Convert.ToHexStringLower(bytes);
    }

    private sealed class DebugView
    {
        private readonly HttpForwarderOptions _target;

        public DebugView(HttpForwarderOptions value)
        {
            _target = value;
        }

        public HttpForwardedHeaderAction ForwardedHeaderAction => _target.ForwardedHeaderAction;
        public HttpForwardedHeaderFormat ForwardedHeaderFormat => _target.ForwardedHeaderFormat;
        public bool PreserveHost => _target.PreserveHost;
        public string? ViaPseudonym => _target.ViaPseudonym;
        public bool AddViaToResponses => _target.AddViaToResponses;
        public TimeSpan ActivityTimeout => _target.ActivityTimeout;
        public Version Version => _target.Version;
        public HttpVersionPolicy VersionPolicy => _target.VersionPolicy;
        public long MaxBufferedResponseBodySize => _target.MaxBufferedResponseBodySize;
    }
}
