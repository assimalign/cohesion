using System;
using System.Collections.Generic;
using System.Net;
using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns.Tests;

public sealed class DnsDelegationCacheTests
{
    [Fact]
    public void TryGetClosest_OnEmptyCache_Misses()
    {
        var cache = new DnsDelegationCache(capacity: 8);
        Assert.False(cache.TryGetClosest("www.example.com", out _, out _));
    }

    [Fact]
    public void Put_ThenTryGetClosest_HitsExactZone()
    {
        var cache = new DnsDelegationCache(8);
        var endpoints = new IPEndPoint[] { new(IPAddress.Loopback, 53) };
        cache.Put("example.com", endpoints, TimeSpan.FromMinutes(5));

        Assert.True(cache.TryGetClosest("example.com", out DnsName? zone, out IReadOnlyList<IPEndPoint>? eps));
        Assert.Equal(new DnsName("example.com"), zone);
        Assert.Equal(endpoints, eps);
    }

    [Fact]
    public void TryGetClosest_FindsAncestorWhenExactMiss()
    {
        var cache = new DnsDelegationCache(8);
        var endpoints = new IPEndPoint[] { new(IPAddress.Loopback, 53) };
        cache.Put("example.com", endpoints, TimeSpan.FromMinutes(5));

        Assert.True(cache.TryGetClosest("www.example.com", out DnsName? zone, out _));
        Assert.Equal(new DnsName("example.com"), zone);

        Assert.True(cache.TryGetClosest("deep.sub.example.com", out zone, out _));
        Assert.Equal(new DnsName("example.com"), zone);
    }

    [Fact]
    public void TryGetClosest_PrefersMostSpecific()
    {
        var cache = new DnsDelegationCache(8);
        var comEndpoints = new IPEndPoint[] { new(IPAddress.Parse("1.1.1.1"), 53) };
        var exampleEndpoints = new IPEndPoint[] { new(IPAddress.Parse("2.2.2.2"), 53) };
        cache.Put("com", comEndpoints, TimeSpan.FromMinutes(5));
        cache.Put("example.com", exampleEndpoints, TimeSpan.FromMinutes(5));

        Assert.True(cache.TryGetClosest("www.example.com", out DnsName? zone, out IReadOnlyList<IPEndPoint>? eps));
        Assert.Equal(new DnsName("example.com"), zone);
        Assert.Equal(exampleEndpoints, eps);

        Assert.True(cache.TryGetClosest("foo.com", out zone, out eps));
        Assert.Equal(new DnsName("com"), zone);
        Assert.Equal(comEndpoints, eps);
    }

    [Fact]
    public void TryGetClosest_AfterExpiration_FallsThroughToAncestor()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new DnsDelegationCache(8, clock);

        cache.Put("com", new IPEndPoint[] { new(IPAddress.Parse("1.1.1.1"), 53) }, TimeSpan.FromMinutes(30));
        cache.Put("example.com", new IPEndPoint[] { new(IPAddress.Parse("2.2.2.2"), 53) }, TimeSpan.FromSeconds(10));

        // After 30s the example.com entry has expired but com is still live.
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(cache.TryGetClosest("www.example.com", out DnsName? zone, out _));
        Assert.Equal(new DnsName("com"), zone);
    }

    [Fact]
    public void Put_ZeroTtl_DoesNotCache()
    {
        var cache = new DnsDelegationCache(8);
        cache.Put("example.com", new IPEndPoint[] { new(IPAddress.Loopback, 53) }, TimeSpan.Zero);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Put_BeyondCapacity_EvictsOldest()
    {
        var cache = new DnsDelegationCache(2);
        cache.Put("a.com", new IPEndPoint[] { new(IPAddress.Loopback, 53) }, TimeSpan.FromMinutes(5));
        cache.Put("b.com", new IPEndPoint[] { new(IPAddress.Loopback, 53) }, TimeSpan.FromMinutes(5));
        cache.Put("c.com", new IPEndPoint[] { new(IPAddress.Loopback, 53) }, TimeSpan.FromMinutes(5));

        Assert.False(cache.TryGetClosest("a.com", out _, out _));
        Assert.True(cache.TryGetClosest("b.com", out _, out _));
        Assert.True(cache.TryGetClosest("c.com", out _, out _));
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        var cache = new DnsDelegationCache(8);
        cache.Put("example.com", new IPEndPoint[] { new(IPAddress.Loopback, 53) }, TimeSpan.FromMinutes(5));
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Constructor_RejectsInvalidCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DnsDelegationCache(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DnsDelegationCache(-1));
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan d) => _now += d;
    }
}
