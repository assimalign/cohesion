using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// Configuration for <see cref="ForwardingDnsResolver"/>.
/// </summary>
public sealed class ForwardingDnsResolverOptions
{
    /// <summary>
    /// Ordered list of upstream forwarders the resolver should try. Each entry is a
    /// <see cref="DnsTransport"/> bound to a specific endpoint; the resolver tries them in
    /// the listed order and fails over on transport failure. The resolver does NOT take
    /// ownership of these transports &#8211; the caller is responsible for their lifetime.
    /// </summary>
    public IList<DnsTransport> Forwarders { get; } = new List<DnsTransport>();

    /// <summary>
    /// Optional UDP&rarr;TCP fallback mapping. When the resolver receives a UDP response with
    /// the TC flag set (RFC 5966), it retries the same question on the mapped TCP transport.
    /// If no mapping exists for a given UDP transport, the truncated response is returned
    /// as-is &#8211; consumers can then retry on TCP themselves.
    /// </summary>
    public IDictionary<DnsTransport, DnsTransport> TcpFallbacks { get; }
        = new Dictionary<DnsTransport, DnsTransport>();

    /// <summary>
    /// End-to-end timeout for one resolve / query call &#8211; covers all forwarder attempts
    /// and the optional TCP fallback. Defaults to 10 seconds.
    /// </summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// EDNS UDP payload size to advertise in the OPT record on outgoing queries
    /// (RFC 6891 &#167; 6.2.3). Defaults to 1232 octets &#8211; the modern guidance that
    /// stays inside a typical Ethernet MTU and avoids fragmentation attacks. Set to zero to
    /// suppress EDNS entirely (compatible with very old resolvers; not recommended).
    /// </summary>
    public ushort EdnsPayloadSize { get; set; } = 1232;

    /// <summary>
    /// When <see langword="true"/>, the resolver caches positive, NXDOMAIN, and NODATA
    /// responses keyed by question, honoring per-record TTL (RFC 1035 &#167; 4.3.2) and
    /// SOA-derived negative TTL (RFC 2308 &#167; 5). Defaults to <see langword="true"/>.
    /// </summary>
    public bool EnableCache { get; set; } = true;

    /// <summary>
    /// Maximum number of entries in the cache. Defaults to 10,000. Ignored when
    /// <see cref="EnableCache"/> is <see langword="false"/>.
    /// </summary>
    public int CacheCapacity { get; set; } = 10_000;

    /// <summary>
    /// Optional floor on cached TTL. When set, an answer with a lower effective TTL is held
    /// in the cache for this long instead. <see langword="null"/> means "use the RR TTL
    /// as-is."
    /// </summary>
    public TimeSpan? MinCacheTtl { get; set; }

    /// <summary>
    /// Optional ceiling on cached TTL. When set, an answer with a higher effective TTL is
    /// capped. <see langword="null"/> means "no ceiling beyond the RR TTL."
    /// </summary>
    public TimeSpan? MaxCacheTtl { get; set; }

    /// <summary>
    /// Time source used by the cache. Defaults to <see cref="TimeProvider.System"/>; tests
    /// can inject <see cref="System.TimeProvider"/> implementations to advance the clock
    /// deterministically.
    /// </summary>
    public TimeProvider? TimeProvider { get; set; }
}
