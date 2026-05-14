using System;
using System.Net;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// Configuration for <see cref="UdpDnsTransport"/>.
/// </summary>
public sealed class UdpDnsTransportOptions
{
    /// <summary>
    /// The remote name server. Required.
    /// </summary>
    public EndPoint? EndPoint { get; set; }

    /// <summary>
    /// Per-exchange timeout. Defaults to 5 seconds, matching the recommended
    /// stub-resolver default in RFC 1123 &#167; 6.1.3.3.
    /// </summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum response size the transport is willing to receive. Defaults to 4096 octets,
    /// matching the EDNS payload size most resolvers advertise. Anything larger comes back
    /// over TCP per RFC 5966 / 7766.
    /// </summary>
    public int MaxResponseSize { get; set; } = 4096;
}
