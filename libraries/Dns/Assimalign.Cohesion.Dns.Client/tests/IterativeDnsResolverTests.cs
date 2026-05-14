using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns.Tests;

public sealed class IterativeDnsResolverTests
{
    [Fact]
    public async Task ResolveAsync_WalksRootToTldToSld()
    {
        await using var root = new LoopbackDnsAuthority(".");
        await using var com = new LoopbackDnsAuthority("com");
        await using var example = new LoopbackDnsAuthority("example.com");

        // NS names are in-bailiwick of the delegated zone so the resolver's strict glue
        // policy can use the glue without recursing to resolve the NS name itself
        // (out-of-bailiwick NS resolution is a PR-6 concern).
        root.Delegate("com", "ns1.com", com);
        com.Delegate("example.com", "ns1.example.com", example);
        example.AddRecord(new DnsARecord("www.example.com", IPAddress.Parse("93.184.216.34"), 300));

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

        // Each authority was hit at least once.
        Assert.True(root.RequestCount >= 1);
        Assert.True(com.RequestCount >= 1);
        Assert.True(example.RequestCount >= 1);
    }

    [Fact]
    public async Task ResolveAsync_NxDomainFromSldIsCachedAndSurfaced()
    {
        await using var root = new LoopbackDnsAuthority(".");
        await using var com = new LoopbackDnsAuthority("com");
        await using var example = new LoopbackDnsAuthority("example.com");

        // NS names are in-bailiwick of the delegated zone so the resolver's strict glue
        // policy can use the glue without recursing to resolve the NS name itself
        // (out-of-bailiwick NS resolution is a PR-6 concern).
        root.Delegate("com", "ns1.com", com);
        com.Delegate("example.com", "ns1.example.com", example);
        // example.com authority returns NXDOMAIN for any unknown name in its zone.

        var resolver = new IterativeDnsResolver(new IterativeDnsResolverOptions
        {
            RootEndpoints = new List<IPEndPoint> { root.VirtualEndPoint },
            UdpTransportFactory = LoopbackDnsAuthority.CreateTransportFactory(),
            TcpTransportFactory = null,
            EnableQNameMinimization = false,
        });

        DnsException ex1 = await Assert.ThrowsAsync<DnsException>(
            () => resolver.ResolveAsync(new DnsQuestion("nope.example.com", DnsRecordType.A)));
        Assert.Equal(DnsErrorCode.NotFound, ex1.Code);

        int sldHitsAfterFirst = example.RequestCount;

        // Second call hits the cache.
        DnsException ex2 = await Assert.ThrowsAsync<DnsException>(
            () => resolver.ResolveAsync(new DnsQuestion("nope.example.com", DnsRecordType.A)));
        Assert.Equal(DnsErrorCode.NotFound, ex2.Code);
        Assert.Equal(sldHitsAfterFirst, example.RequestCount);
    }

    [Fact]
    public async Task ResolveAsync_RejectsOutOfBailiwickReferral()
    {
        // Set up root that delegates "evil-zone.com" to a malicious sibling. The com
        // authority is supposed to delegate that, not root, so the bailiwick check
        // should reject root's referral.
        await using var root = new LoopbackDnsAuthority(".");
        await using var malicious = new LoopbackDnsAuthority("evil-zone.com");

        // Root's delegation lists "example.org" but points the glue at the malicious server.
        // The resolver gets a referral to example.org from root with glue inside the .org —
        // valid bailiwick. But if we ask for example.org, we walk down. Let's instead make
        // root delegate example.org but with NS pointing at an out-of-bailiwick name with
        // out-of-bailiwick glue.
        await using var orgZone = new LoopbackDnsAuthority("example.org");
        root.Delegate("example.org", "ns1.example.org", orgZone);
        // Now make orgZone delegate "child.example.org" but with NS that's
        // out-of-bailiwick — using DelegateRaw to spoof glue.
        orgZone.DelegateRaw(
            "child.example.org",
            nsName: "ns.evil.com",
            glueEndpoint: malicious.EndPoint,
            glueOwner: "ns.evil.com");

        var resolver = new IterativeDnsResolver(new IterativeDnsResolverOptions
        {
            RootEndpoints = new List<IPEndPoint> { root.VirtualEndPoint },
            UdpTransportFactory = LoopbackDnsAuthority.CreateTransportFactory(),
            TcpTransportFactory = null,
            EnableQNameMinimization = false,
        });

        // Query for child.example.org should fail: the orgZone gives a referral to
        // child.example.org with NS=ns.evil.com + glue. The glue is out-of-bailiwick
        // (evil.com is not inside child.example.org), so the resolver discards it. With no
        // other glue, the resolver surfaces a transport error.
        DnsException ex = await Assert.ThrowsAsync<DnsException>(
            () => resolver.ResolveAsync(new DnsQuestion("foo.child.example.org", DnsRecordType.A)));
        Assert.True(ex.Code is DnsErrorCode.Transport or DnsErrorCode.Spoofed,
            $"Expected Transport or Spoofed, got {ex.Code}");
    }

    [Fact]
    public async Task ResolveAsync_QNameMinimization_SendsNsProbesAtEachLevel()
    {
        await using var root = new LoopbackDnsAuthority(".");
        await using var com = new LoopbackDnsAuthority("com");
        await using var example = new LoopbackDnsAuthority("example.com");

        // NS names are in-bailiwick of the delegated zone so the resolver's strict glue
        // policy can use the glue without recursing to resolve the NS name itself
        // (out-of-bailiwick NS resolution is a PR-6 concern).
        root.Delegate("com", "ns1.com", com);
        com.Delegate("example.com", "ns1.example.com", example);
        example.AddRecord(new DnsARecord("www.example.com", IPAddress.Parse("93.184.216.34"), 300));

        var resolver = new IterativeDnsResolver(new IterativeDnsResolverOptions
        {
            RootEndpoints = new List<IPEndPoint> { root.VirtualEndPoint },
            UdpTransportFactory = LoopbackDnsAuthority.CreateTransportFactory(),
            TcpTransportFactory = null,
            EnableQNameMinimization = true,
        });

        DnsMessage answer = await resolver.ResolveAsync(new DnsQuestion("www.example.com", DnsRecordType.A));
        Assert.Single(answer.Answers);

        // With QNAME minimization, root should only see a question for "com" (NS), not the
        // full "www.example.com". This is the whole point of RFC 9156.
        Assert.NotNull(root.LastQuestion);
        Assert.True(
            root.LastQuestion!.Value.Name.Equals(new DnsName("com")),
            $"root saw full QNAME instead of minimized: {root.LastQuestion}");
        Assert.Equal(DnsRecordType.NS, root.LastQuestion!.Value.Type);
    }

    [Fact]
    public async Task ResolveAsync_QNameMinimization_RootSeesFullQNameWhenDisabled()
    {
        await using var root = new LoopbackDnsAuthority(".");
        await using var com = new LoopbackDnsAuthority("com");
        await using var example = new LoopbackDnsAuthority("example.com");

        // NS names are in-bailiwick of the delegated zone so the resolver's strict glue
        // policy can use the glue without recursing to resolve the NS name itself
        // (out-of-bailiwick NS resolution is a PR-6 concern).
        root.Delegate("com", "ns1.com", com);
        com.Delegate("example.com", "ns1.example.com", example);
        example.AddRecord(new DnsARecord("www.example.com", IPAddress.Parse("93.184.216.34"), 300));

        var resolver = new IterativeDnsResolver(new IterativeDnsResolverOptions
        {
            RootEndpoints = new List<IPEndPoint> { root.VirtualEndPoint },
            UdpTransportFactory = LoopbackDnsAuthority.CreateTransportFactory(),
            TcpTransportFactory = null,
            EnableQNameMinimization = false,
        });

        _ = await resolver.ResolveAsync(new DnsQuestion("www.example.com", DnsRecordType.A));

        // Without QNAME min, the root saw the full QNAME.
        Assert.NotNull(root.LastQuestion);
        Assert.Equal(new DnsName("www.example.com"), root.LastQuestion!.Value.Name);
        Assert.Equal(DnsRecordType.A, root.LastQuestion!.Value.Type);
    }

    [Fact]
    public async Task ResolveAsync_ReferralDepthLimit_Bounded()
    {
        await using var root = new LoopbackDnsAuthority(".");

        // Set the depth limit to 1 so even a 2-level walk fails.
        var resolver = new IterativeDnsResolver(new IterativeDnsResolverOptions
        {
            RootEndpoints = new List<IPEndPoint> { root.VirtualEndPoint },
            UdpTransportFactory = LoopbackDnsAuthority.CreateTransportFactory(),
            TcpTransportFactory = null,
            MaxReferralDepth = 1,
            EnableQNameMinimization = false,
        });

        await using var com = new LoopbackDnsAuthority("com");
        await using var example = new LoopbackDnsAuthority("example.com");
        // NS names are in-bailiwick of the delegated zone so the resolver's strict glue
        // policy can use the glue without recursing to resolve the NS name itself
        // (out-of-bailiwick NS resolution is a PR-6 concern).
        root.Delegate("com", "ns1.com", com);
        com.Delegate("example.com", "ns1.example.com", example);
        example.AddRecord(new DnsARecord("www.example.com", IPAddress.Parse("1.2.3.4"), 300));

        DnsException ex = await Assert.ThrowsAsync<DnsException>(
            () => resolver.ResolveAsync(new DnsQuestion("www.example.com", DnsRecordType.A)));
        Assert.Equal(DnsErrorCode.Transport, ex.Code);
    }

    [Fact]
    public async Task ResolveAsync_QueryBudget_Bounded()
    {
        await using var root = new LoopbackDnsAuthority(".");
        await using var com = new LoopbackDnsAuthority("com");
        await using var example = new LoopbackDnsAuthority("example.com");
        // NS names are in-bailiwick of the delegated zone so the resolver's strict glue
        // policy can use the glue without recursing to resolve the NS name itself
        // (out-of-bailiwick NS resolution is a PR-6 concern).
        root.Delegate("com", "ns1.com", com);
        com.Delegate("example.com", "ns1.example.com", example);
        example.AddRecord(new DnsARecord("www.example.com", IPAddress.Parse("1.2.3.4"), 300));

        // Budget of 1 — only the very first query is allowed.
        var resolver = new IterativeDnsResolver(new IterativeDnsResolverOptions
        {
            RootEndpoints = new List<IPEndPoint> { root.VirtualEndPoint },
            UdpTransportFactory = LoopbackDnsAuthority.CreateTransportFactory(),
            TcpTransportFactory = null,
            MaxQueriesPerResolve = 1,
            EnableQNameMinimization = false,
        });

        DnsException ex = await Assert.ThrowsAsync<DnsException>(
            () => resolver.ResolveAsync(new DnsQuestion("www.example.com", DnsRecordType.A)));
        Assert.Equal(DnsErrorCode.Transport, ex.Code);
    }

    [Fact]
    public async Task ResolveAsync_ClearCache_ForcesRefetch()
    {
        await using var root = new LoopbackDnsAuthority(".");
        await using var com = new LoopbackDnsAuthority("com");
        await using var example = new LoopbackDnsAuthority("example.com");
        // NS names are in-bailiwick of the delegated zone so the resolver's strict glue
        // policy can use the glue without recursing to resolve the NS name itself
        // (out-of-bailiwick NS resolution is a PR-6 concern).
        root.Delegate("com", "ns1.com", com);
        com.Delegate("example.com", "ns1.example.com", example);
        example.AddRecord(new DnsARecord("www.example.com", IPAddress.Parse("93.184.216.34"), 300));

        var resolver = new IterativeDnsResolver(new IterativeDnsResolverOptions
        {
            RootEndpoints = new List<IPEndPoint> { root.VirtualEndPoint },
            UdpTransportFactory = LoopbackDnsAuthority.CreateTransportFactory(),
            TcpTransportFactory = null,
            EnableQNameMinimization = false,
        });

        _ = await resolver.ResolveAsync(new DnsQuestion("www.example.com", DnsRecordType.A));
        _ = await resolver.ResolveAsync(new DnsQuestion("www.example.com", DnsRecordType.A));

        // Second call is cached — SLD only hit once.
        int sldHitsBefore = example.RequestCount;
        await resolver.ClearCacheAsync();
        _ = await resolver.ResolveAsync(new DnsQuestion("www.example.com", DnsRecordType.A));
        Assert.True(example.RequestCount > sldHitsBefore);
    }

    [Fact]
    public void Constructor_RejectsEmptyRoots()
    {
        var options = new IterativeDnsResolverOptions();
        options.RootEndpoints.Clear();
        Assert.Throws<ArgumentException>(() => new IterativeDnsResolver(options));
    }

    [Fact]
    public async Task ResolveAsync_ServerFailureFromAuthority_Surfaces()
    {
        await using var root = new LoopbackDnsAuthority(".");
        await using var com = new LoopbackDnsAuthority("com");
        // NS name in-bailiwick of the delegated zone so the strict glue policy accepts it.
        root.Delegate("com", "ns1.com", com);

        // SLD authority is a bare UDP server that returns SERVFAIL for everything.
        await using var brokenSld = new LoopbackUdpDnsServer();
        brokenSld.OnRequest(req =>
        {
            DnsMessage query = DnsTestMessages.ParseQuery(req);
            return DnsTestMessages.BuildWithRcode(query, DnsResponseCode.ServFail);
        });
        IPEndPoint brokenVirtual = LoopbackDnsAuthority.RegisterVirtual(brokenSld);

        // Inject a delegation with in-bailiwick glue pointing at the broken SLD's virtual
        // address (the transport factory translates it back to the ephemeral port).
        com.DelegateRaw(
            "example.com",
            nsName: "ns1.example.com",
            glueEndpoint: brokenVirtual,
            glueOwner: "ns1.example.com");

        var resolver = new IterativeDnsResolver(new IterativeDnsResolverOptions
        {
            RootEndpoints = new List<IPEndPoint> { root.VirtualEndPoint },
            UdpTransportFactory = LoopbackDnsAuthority.CreateTransportFactory(),
            TcpTransportFactory = null,
            EnableQNameMinimization = false,
        });

        DnsException ex = await Assert.ThrowsAsync<DnsException>(
            () => resolver.ResolveAsync(new DnsQuestion("www.example.com", DnsRecordType.A)));
        Assert.Equal(DnsErrorCode.ServerFailure, ex.Code);
    }
}
