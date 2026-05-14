using System;
using System.Net;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// Configuration for <see cref="TcpDnsTransport"/>.
/// </summary>
public sealed class TcpDnsTransportOptions
{
    /// <summary>
    /// The remote name server. Required.
    /// </summary>
    public EndPoint? EndPoint { get; set; }

    /// <summary>
    /// TCP connect timeout. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Per-exchange timeout (covers send + receive of one exchange). Defaults to 5 seconds.
    /// </summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long an idle TCP connection is held open before being recycled. RFC 7766 &#167; 6.2.3
    /// recommends a finite idle close so servers can reclaim resources. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
