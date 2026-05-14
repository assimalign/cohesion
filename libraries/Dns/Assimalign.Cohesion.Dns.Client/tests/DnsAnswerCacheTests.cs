using System;
using System.Collections.Generic;
using System.Net;
using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns.Tests;

public sealed class DnsAnswerCacheTests
{
    [Fact]
    public void TryGet_OnEmptyCache_Misses()
    {
        var cache = new DnsAnswerCache(capacity: 8, minTtl: null, maxTtl: null);
        Assert.False(cache.TryGet(new DnsQuestion("example.com", DnsRecordType.A), out _));
    }

    [Fact]
    public void Put_ThenTryGet_ReturnsStoredMessage()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new DnsAnswerCache(8, null, null, clock);

        DnsMessage message = BuildPositive("example.com", ttl: 300);
        cache.Put(new DnsQuestion("example.com", DnsRecordType.A), message);

        Assert.True(cache.TryGet(new DnsQuestion("example.com", DnsRecordType.A), out DnsMessage? hit));
        Assert.Same(message, hit);
    }

    [Fact]
    public void TryGet_AfterTtlElapses_EvictsEntry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new DnsAnswerCache(8, null, null, clock);

        DnsMessage message = BuildPositive("example.com", ttl: 30);
        cache.Put(new DnsQuestion("example.com", DnsRecordType.A), message);

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(cache.TryGet(new DnsQuestion("example.com", DnsRecordType.A), out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Put_NameDifferingOnlyInCase_AliasesSameEntry()
    {
        var cache = new DnsAnswerCache(8, null, null);
        cache.Put(new DnsQuestion("Example.COM", DnsRecordType.A), BuildPositive("example.com", 300));

        Assert.True(cache.TryGet(new DnsQuestion("example.com", DnsRecordType.A), out _));
        Assert.True(cache.TryGet(new DnsQuestion("EXAMPLE.com", DnsRecordType.A), out _));
        Assert.True(cache.TryGet(new DnsQuestion("example.com.", DnsRecordType.A), out _));
    }

    [Fact]
    public void Put_DifferingTypes_ShareKeyspaceCorrectly()
    {
        var cache = new DnsAnswerCache(8, null, null);
        cache.Put(new DnsQuestion("example.com", DnsRecordType.A), BuildPositive("example.com", 300));

        Assert.True(cache.TryGet(new DnsQuestion("example.com", DnsRecordType.A), out _));
        Assert.False(cache.TryGet(new DnsQuestion("example.com", DnsRecordType.AAAA), out _));
    }

    [Fact]
    public void Put_BeyondCapacity_EvictsOldest()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new DnsAnswerCache(2, null, null, clock);

        cache.Put(new DnsQuestion("a.example.com", DnsRecordType.A), BuildPositive("a.example.com", 300));
        cache.Put(new DnsQuestion("b.example.com", DnsRecordType.A), BuildPositive("b.example.com", 300));
        cache.Put(new DnsQuestion("c.example.com", DnsRecordType.A), BuildPositive("c.example.com", 300));

        Assert.False(cache.TryGet(new DnsQuestion("a.example.com", DnsRecordType.A), out _));
        Assert.True(cache.TryGet(new DnsQuestion("b.example.com", DnsRecordType.A), out _));
        Assert.True(cache.TryGet(new DnsQuestion("c.example.com", DnsRecordType.A), out _));
    }

    [Fact]
    public void Put_ZeroTtl_DoesNotCache()
    {
        var cache = new DnsAnswerCache(8, null, null);
        cache.Put(new DnsQuestion("example.com", DnsRecordType.A), BuildPositive("example.com", ttl: 0));
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(new DnsQuestion("example.com", DnsRecordType.A), out _));
    }

    [Fact]
    public void Put_MinTtl_ExtendsShortTtl()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new DnsAnswerCache(8, minTtl: TimeSpan.FromSeconds(60), maxTtl: null, clock);

        // Record TTL is 5s but MinTtl floor is 60s.
        cache.Put(new DnsQuestion("example.com", DnsRecordType.A), BuildPositive("example.com", ttl: 5));

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(cache.TryGet(new DnsQuestion("example.com", DnsRecordType.A), out _));

        clock.Advance(TimeSpan.FromSeconds(31)); // total 61s
        Assert.False(cache.TryGet(new DnsQuestion("example.com", DnsRecordType.A), out _));
    }

    [Fact]
    public void Put_MaxTtl_CapsLongTtl()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new DnsAnswerCache(8, minTtl: null, maxTtl: TimeSpan.FromSeconds(60), clock);

        // Record TTL is 3600s but MaxTtl caps to 60s.
        cache.Put(new DnsQuestion("example.com", DnsRecordType.A), BuildPositive("example.com", ttl: 3600));

        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.False(cache.TryGet(new DnsQuestion("example.com", DnsRecordType.A), out _));
    }

    [Fact]
    public void Put_NxDomainWithSoa_CachedForSoaNegativeTtl()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new DnsAnswerCache(8, null, null, clock);

        DnsSoaRecord soa = DnsTestMessages.ExampleComSoa(minimumTtl: 30, ttl: 60);
        DnsMessage nx = BuildNxDomain("nope.example.com", soa);

        cache.Put(new DnsQuestion("nope.example.com", DnsRecordType.A), nx);

        // negative TTL = min(SOA.TTL=60, SOA.MINIMUM=30) = 30
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.True(cache.TryGet(new DnsQuestion("nope.example.com", DnsRecordType.A), out _));

        clock.Advance(TimeSpan.FromSeconds(15)); // total 35s, past the 30s SOA-derived TTL
        Assert.False(cache.TryGet(new DnsQuestion("nope.example.com", DnsRecordType.A), out _));
    }

    [Fact]
    public void Put_NxDomainWithoutSoa_IsNotCached()
    {
        var cache = new DnsAnswerCache(8, null, null);

        var header = new DnsHeader(
            id: 0x1234,
            DnsHeaderFlags.Response | DnsHeaderFlags.RecursionAvailable,
            DnsOpCode.Query,
            DnsResponseCode.NXDomain,
            1, 0, 0, 0);

        var nx = new DnsMessage(
            header,
            new[] { new DnsQuestion("nope.example.com", DnsRecordType.A) },
            Array.Empty<DnsRecord>(),
            Array.Empty<DnsRecord>(),
            Array.Empty<DnsRecord>());

        cache.Put(new DnsQuestion("nope.example.com", DnsRecordType.A), nx);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Put_NoDataWithSoa_CachedForSoaNegativeTtl()
    {
        // NoError + empty answer + SOA in authority = NODATA per RFC 2308 §2.2.
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new DnsAnswerCache(8, null, null, clock);

        DnsSoaRecord soa = DnsTestMessages.ExampleComSoa(minimumTtl: 45, ttl: 90);
        var header = new DnsHeader(
            id: 0x1111,
            DnsHeaderFlags.Response | DnsHeaderFlags.RecursionAvailable,
            DnsOpCode.Query,
            DnsResponseCode.NoError,
            1, 0, 1, 0);

        var nodata = new DnsMessage(
            header,
            new[] { new DnsQuestion("example.com", DnsRecordType.AAAA) },
            Array.Empty<DnsRecord>(),
            new DnsRecord[] { soa },
            Array.Empty<DnsRecord>());

        cache.Put(new DnsQuestion("example.com", DnsRecordType.AAAA), nodata);

        clock.Advance(TimeSpan.FromSeconds(40));
        Assert.True(cache.TryGet(new DnsQuestion("example.com", DnsRecordType.AAAA), out _));

        clock.Advance(TimeSpan.FromSeconds(10)); // 50s > min(SOA.TTL=90, SOA.MINIMUM=45)=45
        Assert.False(cache.TryGet(new DnsQuestion("example.com", DnsRecordType.AAAA), out _));
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        var cache = new DnsAnswerCache(8, null, null);
        cache.Put(new DnsQuestion("a.example.com", DnsRecordType.A), BuildPositive("a.example.com", 300));
        cache.Put(new DnsQuestion("b.example.com", DnsRecordType.A), BuildPositive("b.example.com", 300));

        cache.Clear();
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Constructor_RejectsInvalidCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DnsAnswerCache(0, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DnsAnswerCache(-1, null, null));
    }

    [Fact]
    public void Constructor_RejectsInvalidTtls()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DnsAnswerCache(8, minTtl: TimeSpan.FromSeconds(-1), null));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DnsAnswerCache(8, null, maxTtl: TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentException>(
            () => new DnsAnswerCache(8, minTtl: TimeSpan.FromSeconds(120), maxTtl: TimeSpan.FromSeconds(60)));
    }

    private static DnsMessage BuildPositive(string name, uint ttl)
    {
        var header = new DnsHeader(
            id: 1,
            DnsHeaderFlags.Response | DnsHeaderFlags.RecursionAvailable,
            DnsOpCode.Query,
            DnsResponseCode.NoError,
            1, 1, 0, 0);

        return new DnsMessage(
            header,
            new[] { new DnsQuestion(name, DnsRecordType.A) },
            new DnsRecord[] { new DnsARecord(name, IPAddress.Parse("127.0.0.1"), ttl) },
            Array.Empty<DnsRecord>(),
            Array.Empty<DnsRecord>());
    }

    private static DnsMessage BuildNxDomain(string name, DnsSoaRecord soa)
    {
        var header = new DnsHeader(
            id: 2,
            DnsHeaderFlags.Response | DnsHeaderFlags.RecursionAvailable,
            DnsOpCode.Query,
            DnsResponseCode.NXDomain,
            1, 0, 1, 0);

        return new DnsMessage(
            header,
            new[] { new DnsQuestion(name, DnsRecordType.A) },
            Array.Empty<DnsRecord>(),
            new DnsRecord[] { soa },
            Array.Empty<DnsRecord>());
    }

    /// <summary>
    /// Minimal fake TimeProvider for cache TTL tests.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
