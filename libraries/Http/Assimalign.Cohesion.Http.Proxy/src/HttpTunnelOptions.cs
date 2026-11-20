using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Configures an <see cref="HttpTunnel"/>.
/// </summary>
/// <remarks>
/// <see cref="HttpTunnel.Create(HttpTunnelOptions?)"/> copies the values, so changing an options
/// instance afterwards does not change a tunnel created from it.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("HttpTunnelOptions")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class HttpTunnelOptions
{
    private static readonly TimeSpan _maximumTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1.0);

    private TimeSpan _connectTimeout = TimeSpan.FromSeconds(15);
    private TimeSpan _idleTimeout = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Gets or sets how long the tunnel waits for the connection to the destination. The default is
    /// 15 seconds; <see cref="Timeout.InfiniteTimeSpan"/> disables it. A connection that is not made
    /// in time is answered with 504 Gateway Timeout.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is not positive, is longer than about 49.7 days, and is not
    /// <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    public TimeSpan ConnectTimeout
    {
        get => _connectTimeout;
        set => _connectTimeout = Validate(value);
    }

    /// <summary>
    /// Gets or sets how long an open tunnel may carry no bytes in either direction before it is
    /// closed. The default is <see cref="Timeout.InfiniteTimeSpan"/>: a tunnel usually carries TLS, and
    /// the proxy cannot see the keep-alives inside it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is not positive, is longer than about 49.7 days, and is not
    /// <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </exception>
    public TimeSpan IdleTimeout
    {
        get => _idleTimeout;
        set => _idleTimeout = Validate(value);
    }

    /// <summary>
    /// Gets or sets the callback that connects to the destination, or <see langword="null"/> to open a
    /// TCP connection to it.
    /// </summary>
    /// <remarks>
    /// The callback owns name resolution, so it is where a policy that refuses some addresses
    /// belongs: the address it checks is then the address it connects to. A
    /// <see cref="System.Net.Sockets.SocketException"/> or <see cref="IOException"/> from it is
    /// answered with 502 Bad Gateway. The tunnel disposes the returned stream when it closes, and
    /// half-closes it after the client finishes sending when it is a
    /// <see cref="System.Net.Sockets.NetworkStream"/>.
    /// </remarks>
    public Func<DnsEndPoint, CancellationToken, ValueTask<Stream>>? ConnectCallback { get; set; }

    private static TimeSpan Validate(TimeSpan value)
    {
        if (value != Timeout.InfiniteTimeSpan && (value <= TimeSpan.Zero || value > _maximumTimeout))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return value;
    }

    private sealed class DebugView
    {
        private readonly HttpTunnelOptions _target;

        public DebugView(HttpTunnelOptions value)
        {
            _target = value;
        }

        public TimeSpan ConnectTimeout => _target.ConnectTimeout;
        public TimeSpan IdleTimeout => _target.IdleTimeout;
        public Func<DnsEndPoint, CancellationToken, ValueTask<Stream>>? ConnectCallback => _target.ConnectCallback;
    }
}
