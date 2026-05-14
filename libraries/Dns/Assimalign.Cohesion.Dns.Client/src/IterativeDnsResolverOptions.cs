using System;
using System.Collections.Generic;
using System.Net;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// Configuration for <see cref="IterativeDnsResolver"/>.
/// </summary>
public sealed class IterativeDnsResolverOptions
{
    /// <summary>
    /// Root server endpoints used as the starting point for every iterative walk.
    /// Defaults to the IANA root endpoint set returned by <see cref="DnsRootHints.Iana"/>
    /// (IPv4 + IPv6, port 53). Assign a fresh list to override (the collection-initializer
    /// shorthand <c>RootEndpoints = { x }</c> would <em>append</em> to the IANA defaults,
    /// not replace them).
    /// </summary>
    public IList<IPEndPoint> RootEndpoints { get; set; } = new List<IPEndPoint>(DnsRootHints.Iana());

    /// <summary>
    /// Factory used to create a UDP transport for an arbitrary endpoint discovered during the
    /// walk. Returns a fresh transport per call; the resolver disposes it after use.
    /// </summary>
    /// <remarks>
    /// Default constructs a <see cref="UdpDnsTransport"/> with a 2-second per-exchange
    /// timeout. Override in tests to inject loopback fakes.
    /// </remarks>
    public Func<IPEndPoint, DnsTransport> UdpTransportFactory { get; set; } = DefaultUdpFactory;

    /// <summary>
    /// Optional factory for a TCP transport used when an authority returns a truncated
    /// response (RFC 5966). Pass <see langword="null"/> to disable TCP fallback &#8211;
    /// truncated responses then surface to the caller as-is.
    /// </summary>
    public Func<IPEndPoint, DnsTransport>? TcpTransportFactory { get; set; } = DefaultTcpFactory;

    /// <summary>
    /// End-to-end timeout for one resolve call &#8211; spans every step in the iterative
    /// walk. Defaults to 15 seconds.
    /// </summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// EDNS UDP payload size to advertise via OPT (RFC 6891 &#167; 6.2.3). Defaults to 1232
    /// octets. Set to zero to suppress the OPT record.
    /// </summary>
    public ushort EdnsPayloadSize { get; set; } = 1232;

    /// <summary>
    /// Maximum number of zones the resolver can walk through for one query. Bounds the
    /// outer loop in case of pathological delegation chains. Default 30.
    /// </summary>
    public int MaxReferralDepth { get; set; } = 30;

    /// <summary>
    /// Maximum number of upstream exchanges allowed per resolve call. Bounds total work
    /// including NS-name resolution detours (when implemented). Default 50.
    /// </summary>
    public int MaxQueriesPerResolve { get; set; } = 50;

    /// <summary>
    /// When <see langword="true"/>, the resolver applies QNAME minimization per RFC 9156:
    /// at each delegation step it asks only for the next label toward the full QNAME, not
    /// the full QNAME itself. This reduces information leakage to intermediate authorities.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool EnableQNameMinimization { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, positive / NXDOMAIN / NODATA answers are cached by
    /// question per RFC 1035 &#167; 4.3.2 and RFC 2308 &#167; 5. Defaults to <see langword="true"/>.
    /// </summary>
    public bool EnableCache { get; set; } = true;

    /// <summary>Maximum cache entries. Defaults to 10,000.</summary>
    public int CacheCapacity { get; set; } = 10_000;

    /// <summary>Optional floor on cached TTL.</summary>
    public TimeSpan? MinCacheTtl { get; set; }

    /// <summary>Optional ceiling on cached TTL.</summary>
    public TimeSpan? MaxCacheTtl { get; set; }

    /// <summary>Time source used by the cache. Defaults to <see cref="System.TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; set; }

    private static DnsTransport DefaultUdpFactory(IPEndPoint endpoint)
        => new UdpDnsTransport(new UdpDnsTransportOptions
        {
            EndPoint = endpoint,
            QueryTimeout = TimeSpan.FromSeconds(2),
        });

    private static DnsTransport DefaultTcpFactory(IPEndPoint endpoint)
        => new TcpDnsTransport(new TcpDnsTransportOptions
        {
            EndPoint = endpoint,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            QueryTimeout = TimeSpan.FromSeconds(3),
            IdleTimeout = TimeSpan.FromSeconds(5),
        });
}
