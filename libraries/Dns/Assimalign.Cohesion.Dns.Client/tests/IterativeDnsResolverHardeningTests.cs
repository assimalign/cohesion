using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns.Tests;

/// <summary>
/// Tests for the PR-6 hardening additions to <see cref="IterativeDnsResolver"/>:
/// out-of-bailiwick NS resolution and delegation caching.
/// </summary>
public sealed class IterativeDnsResolverHardeningTests
{
    /// <summary>
    /// Real-world style: the .com authority delegates example.com to ns1.example-dns.net.
    /// The NS name lives in a different zone with no glue available from .com. The resolver
    /// must recursively resolve ns1.example-dns.net via the same walk and reuse the result.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_ResolvesOutOfBailiwickNsName()
    {
        await using var root = new LoopbackDnsAuthority(".");
        await using var com = new LoopbackDnsAuthority("com");
        await using var dnsNet = new LoopbackDnsAuthority("example-dns.net");
        await using var net = new LoopbackDnsAuthority("net");
        await using var exampleCom = new LoopbackDnsAuthority("example.com");

        root.Delegate("com", "ns1.com", com);
        root.Delegate("net", "ns1.net", net);

        // .net delegates example-dns.net with in-bailiwick glue so the resolver can find
        // the NS-hosting authority on its own.
        net.Delegate("example-dns.net", "ns1.example-dns.net", dnsNet);

        // The example-dns.net zone holds an A record for the NS name we'll see referenced
        // from .com — this is the out-of-bailiwick NS the resolver must resolve.
        dnsNet.AddRecord(new DnsARecord(
            "ns1.example-dns.net",
            exampleCom.VirtualEndPoint.Address,
            300));

        // .com delegates example.com using an out-of-bailiwick NS name (the name lives in
        // example-dns.net, not .com). No glue is provided by .com because the strict glue
        // policy would discard it anyway — the resolver must resolve it.
        com.Delegate("example.com", "ns1.example-dns.net", exampleCom, includeGlue: false);

        exampleCom.AddRecord(new DnsARecord("www.example.com", IPAddress.Parse("93.184.216.34"), 300));

        var resolver = new IterativeDnsResolver(new IterativeDnsResolverOptions
        {
            RootEndpoints = new List<IPEndPoint> { root.VirtualEndPoint },
            UdpTransportFactory = LoopbackDnsAuthority.CreateTransportFactory(),
            TcpTransportFactory = null,
            EnableQNameMinimization = false,
        });

        DnsMessage answer = await resolver.ResolveAsync(new DnsQuestion("www.example.com", DnsRecordType.A));
        Assert.Single(answer.Answers);
        DnsARecord a = Assert.IsType<DnsARecord>(answer.Answers[0]);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), a.Address);
    }

    /// <summary>
    /// Out-of-bailiwick NS resolution is bounded by <see
    /// cref="IterativeDnsResolverOptions.MaxNsResolutionDepth"/> so a pathological zone
    /// requiring deep NS-name chasing can't blow the query budget.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_NsResolutionDepth_Bounded()
    {
        await using var root = new LoopbackDnsAuthority(".");
        await using var com = new LoopbackDnsAuthority("com");
        // Delegation with out-of-bailiwick NS and no glue, plus depth 0 means the resolver
        // is not allowed to do any NS-name lookups at all.
        root.Delegate("com", "ns1.com", com);
        com.Delegate("example.com", "ns1.example-dns.net", new LoopbackDnsAuthority("example.com"), includeGlue: false);

        var resolver = new IterativeDnsResolver(new IterativeDnsResolverOptions
        {
            RootEndpoints = new List<IPEndPoint> { root.VirtualEndPoint },
            UdpTransportFactory = LoopbackDnsAuthority.CreateTransportFactory(),
            TcpTransportFactory = null,
            EnableQNameMinimization = false,
            MaxNsResolutionDepth = 0,
        });

        DnsException ex = await Assert.ThrowsAsync<DnsException>(
            () => resolver.ResolveAsync(new DnsQuestion("www.example.com", DnsRecordType.A)));
        Assert.Equal(DnsErrorCode.Transport, ex.Code);
    }

    /// <summary>
    /// Second query for a name under a previously-resolved zone should hit the delegation
    /// cache and skip the root&rarr;TLD walk. Verify by counting how many times the root
    /// authority is contacted across two resolves.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_DelegationCache_SkipsRootWalkOnSecondQuery()
    {
        await using var root = new LoopbackDnsAuthority(".");
        await using var com = new LoopbackDnsAuthority("com");
        await using var example = new LoopbackDnsAuthority("example.com");
        root.Delegate("com", "ns1.com", com);
        com.Delegate("example.com", "ns1.example.com", example);
        example.AddRecord(new DnsARecord("a.example.com", IPAddress.Parse("1.2.3.4"), 300));
        example.AddRecord(new DnsARecord("b.example.com", IPAddress.Parse("5.6.7.8"), 300));

        var resolver = new IterativeDnsResolver(new IterativeDnsResolverOptions
        {
            RootEndpoints = new List<IPEndPoint> { root.VirtualEndPoint },
            UdpTransportFactory = LoopbackDnsAuthority.CreateTransportFactory(),
            TcpTransportFactory = null,
            EnableQNameMinimization = false,
        });

        _ = await resolver.ResolveAsync(new DnsQuestion("a.example.com", DnsRecordType.A));
        int rootHitsAfterFirst = root.RequestCount;
        int comHitsAfterFirst = com.RequestCount;

        // Second query for a DIFFERENT name in example.com: should NOT walk through root or
        // com, but go directly to example.com via the cached delegation.
        _ = await resolver.ResolveAsync(new DnsQuestion("b.example.com", DnsRecordType.A));

        Assert.Equal(rootHitsAfterFirst, root.RequestCount);
        Assert.Equal(comHitsAfterFirst, com.RequestCount);
        Assert.True(resolver.DelegationCacheCount > 0);
    }

    [Fact]
    public async Task ResolveAsync_DelegationCache_ClearedByClearCacheAsync()
    {
        await using var root = new LoopbackDnsAuthority(".");
        await using var com = new LoopbackDnsAuthority("com");
        await using var example = new LoopbackDnsAuthority("example.com");
        root.Delegate("com", "ns1.com", com);
        com.Delegate("example.com", "ns1.example.com", example);
        example.AddRecord(new DnsARecord("www.example.com", IPAddress.Parse("1.2.3.4"), 300));

        var resolver = new IterativeDnsResolver(new IterativeDnsResolverOptions
        {
            RootEndpoints = new List<IPEndPoint> { root.VirtualEndPoint },
            UdpTransportFactory = LoopbackDnsAuthority.CreateTransportFactory(),
            TcpTransportFactory = null,
            EnableQNameMinimization = false,
        });

        _ = await resolver.ResolveAsync(new DnsQuestion("www.example.com", DnsRecordType.A));
        Assert.True(resolver.DelegationCacheCount > 0);

        await resolver.ClearCacheAsync();
        Assert.Equal(0, resolver.DelegationCacheCount);
        Assert.Equal(0, resolver.CacheCount);
    }

    [Fact]
    public async Task ResolveAsync_DelegationCacheDisabled_AlwaysWalksFromRoot()
    {
        await using var root = new LoopbackDnsAuthority(".");
        await using var com = new LoopbackDnsAuthority("com");
        await using var example = new LoopbackDnsAuthority("example.com");
        root.Delegate("com", "ns1.com", com);
        com.Delegate("example.com", "ns1.example.com", example);
        example.AddRecord(new DnsARecord("a.example.com", IPAddress.Parse("1.2.3.4"), 300));
        example.AddRecord(new DnsARecord("b.example.com", IPAddress.Parse("5.6.7.8"), 300));

        var resolver = new IterativeDnsResolver(new IterativeDnsResolverOptions
        {
            RootEndpoints = new List<IPEndPoint> { root.VirtualEndPoint },
            UdpTransportFactory = LoopbackDnsAuthority.CreateTransportFactory(),
            TcpTransportFactory = null,
            EnableQNameMinimization = false,
            EnableDelegationCache = false,
        });

        _ = await resolver.ResolveAsync(new DnsQuestion("a.example.com", DnsRecordType.A));
        int rootHitsAfterFirst = root.RequestCount;

        _ = await resolver.ResolveAsync(new DnsQuestion("b.example.com", DnsRecordType.A));

        // No delegation cache means the second query also walks through root.
        Assert.True(root.RequestCount > rootHitsAfterFirst);
        Assert.Equal(0, resolver.DelegationCacheCount);
    }
}
