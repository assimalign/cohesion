using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Caching;
using Assimalign.Cohesion.Caching.InMemory;

namespace Assimalign.Cohesion.Caching.InMemory.Tests;

/// <summary>
/// Contract-level compatibility coverage: every behavior the root <see cref="ICache"/> contract
/// requires of any implementation, exercised against <see cref="MemoryCache"/>. New cache
/// implementations should mirror these assertions so foundation expectations stay locked.
/// </summary>
public class CacheContractComplianceTests
{
    public static IEnumerable<object[]> CacheFactories => new[]
    {
        new object[] { (Func<ICache>)(() => new MemoryCache()) },
    };

    [Theory(DisplayName = "Cohesion Contract - CreateEntry returns entry with caller-provided key")]
    [MemberData(nameof(CacheFactories))]
    public void CreateEntry_ReturnsEntryWithKey(Func<ICache> factory)
    {
        using var cache = factory();

        using var entry = cache.CreateEntry("k");

        Assert.Equal("k", entry.Key);
    }

    [Theory(DisplayName = "Cohesion Contract - CreateEntry rejects null key")]
    [MemberData(nameof(CacheFactories))]
    public void CreateEntry_NullKey_Throws(Func<ICache> factory)
    {
        using var cache = factory();
        Assert.Throws<ArgumentNullException>(() => cache.CreateEntry(null!));
    }

    [Theory(DisplayName = "Cohesion Contract - TryGetValue rejects null key")]
    [MemberData(nameof(CacheFactories))]
    public void TryGetValue_NullKey_Throws(Func<ICache> factory)
    {
        using var cache = factory();
        Assert.Throws<ArgumentNullException>(() => cache.TryGetValue(null!, out _));
    }

    [Theory(DisplayName = "Cohesion Contract - Remove rejects null key")]
    [MemberData(nameof(CacheFactories))]
    public void Remove_NullKey_Throws(Func<ICache> factory)
    {
        using var cache = factory();
        Assert.Throws<ArgumentNullException>(() => cache.Remove(null!));
    }

    [Theory(DisplayName = "Cohesion Contract - Set then Get round-trips the value")]
    [MemberData(nameof(CacheFactories))]
    public void Set_Get_RoundTrips(Func<ICache> factory)
    {
        using var cache = factory();

        cache.Set("k", 42);

        Assert.True(cache.TryGetValue("k", out var value));
        Assert.Equal(42, value);
    }

    [Theory(DisplayName = "Cohesion Contract - Removed entry is gone")]
    [MemberData(nameof(CacheFactories))]
    public void Remove_RemovedEntryIsGone(Func<ICache> factory)
    {
        using var cache = factory();

        cache.Set("k", 42);
        cache.Remove("k");

        Assert.False(cache.TryGetValue("k", out _));
    }

    [Theory(DisplayName = "Cohesion Contract - Cleared cache has no entries")]
    [MemberData(nameof(CacheFactories))]
    public void Clear_RemovesEveryEntry(Func<ICache> factory)
    {
        using var cache = factory();

        cache.Set(1, "a");
        cache.Set(2, "b");
        cache.Set(3, "c");

        cache.Clear();

        Assert.False(cache.TryGetValue(1, out _));
        Assert.False(cache.TryGetValue(2, out _));
        Assert.False(cache.TryGetValue(3, out _));
    }

    [Theory(DisplayName = "Cohesion Contract - Disposed cache rejects further operations")]
    [MemberData(nameof(CacheFactories))]
    public void Dispose_FurtherOperationsThrow(Func<ICache> factory)
    {
        var cache = factory();
        cache.Dispose();

        Assert.ThrowsAny<Exception>(() => cache.TryGetValue("k", out _));
    }

    [Theory(DisplayName = "Cohesion Contract - Entry not committed until disposed")]
    [MemberData(nameof(CacheFactories))]
    public void Entry_NotCommittedUntilDispose(Func<ICache> factory)
    {
        using var cache = factory();

        var entry = cache.CreateEntry("k");
        entry.Value = "v";

        Assert.False(cache.TryGetValue("k", out _));

        entry.Dispose();
        Assert.True(cache.TryGetValue("k", out _));
    }
}
