using System.Collections.Generic;
using System.Net;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// IANA root server endpoints, used as the starting point for iterative DNS resolution.
/// </summary>
/// <remarks>
/// <para>
/// The root server set is intentionally hardcoded: the IPs change rarely (last meaningful
/// change was 2017 for L-root) and consulting an external source to bootstrap the resolver
/// would defeat the purpose of having root hints in the first place. The current addresses
/// are pulled from
/// <see href="https://www.internic.net/zones/named.root">named.root</see> &#8211;
/// the IANA-maintained reference file.
/// </para>
/// <para>
/// For private-DNS / split-horizon deployments, construct
/// <see cref="IterativeDnsResolver"/> with a custom
/// <see cref="IterativeDnsResolverOptions.RootEndpoints"/> list pointing at the local root
/// authorities instead of these.
/// </para>
/// </remarks>
public static class DnsRootHints
{
    /// <summary>
    /// Returns IPv4 + IPv6 endpoints for all 13 IANA root servers, port 53. Each name
    /// contributes its IPv4 endpoint immediately followed by its IPv6 endpoint, so a resolver
    /// that iterates in order alternates protocols and falls over gracefully when one
    /// stack is unavailable.
    /// </summary>
    public static IReadOnlyList<IPEndPoint> Iana()
    {
        var v4 = IanaIPv4();
        var v6 = IanaIPv6();
        var result = new List<IPEndPoint>(v4.Count + v6.Count);
        for (int i = 0; i < v4.Count; i++)
        {
            result.Add(v4[i]);
            result.Add(v6[i]);
        }
        return result;
    }

    /// <summary>Returns the 13 IPv4 IANA root endpoints on port 53.</summary>
    public static IReadOnlyList<IPEndPoint> IanaIPv4() => new IPEndPoint[]
    {
        new(IPAddress.Parse("198.41.0.4"),     53), // a
        new(IPAddress.Parse("170.247.170.2"),  53), // b
        new(IPAddress.Parse("192.33.4.12"),    53), // c
        new(IPAddress.Parse("199.7.91.13"),    53), // d
        new(IPAddress.Parse("192.203.230.10"), 53), // e
        new(IPAddress.Parse("192.5.5.241"),    53), // f
        new(IPAddress.Parse("192.112.36.4"),   53), // g
        new(IPAddress.Parse("198.97.190.53"),  53), // h
        new(IPAddress.Parse("192.36.148.17"),  53), // i
        new(IPAddress.Parse("192.58.128.30"),  53), // j
        new(IPAddress.Parse("193.0.14.129"),   53), // k
        new(IPAddress.Parse("199.7.83.42"),    53), // l
        new(IPAddress.Parse("202.12.27.33"),   53), // m
    };

    /// <summary>Returns the 13 IPv6 IANA root endpoints on port 53.</summary>
    public static IReadOnlyList<IPEndPoint> IanaIPv6() => new IPEndPoint[]
    {
        new(IPAddress.Parse("2001:503:ba3e::2:30"), 53), // a
        new(IPAddress.Parse("2801:1b8:10::b"),      53), // b
        new(IPAddress.Parse("2001:500:2::c"),       53), // c
        new(IPAddress.Parse("2001:500:2d::d"),      53), // d
        new(IPAddress.Parse("2001:500:a8::e"),      53), // e
        new(IPAddress.Parse("2001:500:2f::f"),      53), // f
        new(IPAddress.Parse("2001:500:12::d0d"),    53), // g
        new(IPAddress.Parse("2001:500:1::53"),      53), // h
        new(IPAddress.Parse("2001:7fe::53"),        53), // i
        new(IPAddress.Parse("2001:503:c27::2:30"),  53), // j
        new(IPAddress.Parse("2001:7fd::1"),         53), // k
        new(IPAddress.Parse("2001:500:9f::42"),     53), // l
        new(IPAddress.Parse("2001:dc3::35"),        53), // m
    };
}
