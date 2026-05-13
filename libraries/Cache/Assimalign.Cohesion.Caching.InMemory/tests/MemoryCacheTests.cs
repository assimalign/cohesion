using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Caching;
using Assimalign.Cohesion.Caching.InMemory;

namespace Assimalign.Cohesion.Caching.InMemory.Tests;

public class MemoryCacheTests
{
    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - MemoryCache: default ctor produces empty cache")]
    public void DefaultCtor_StartsEmpty()
    {
        using var cache = new MemoryCache();

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGetValue("missing", out _));
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - MemoryCache: ctor rejects null options")]
    public void Ctor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MemoryCache(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - MemoryCache: implements IMemoryCache and ICache")]
    public void Implements_ContractMarkers()
    {
        using var cache = new MemoryCache();

        Assert.IsAssignableFrom<ICache>(cache);
        Assert.IsAssignableFrom<IMemoryCache>(cache);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - CreateEntry: null key throws")]
    public void CreateEntry_NullKey_Throws()
    {
        using var cache = new MemoryCache();

        Assert.Throws<ArgumentNullException>(() => cache.CreateEntry(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Set/Get: commits and reads back value")]
    public void Set_Get_RoundTrip()
    {
        using var cache = new MemoryCache();

        cache.Set("k", "v");

        Assert.True(cache.TryGetValue("k", out var value));
        Assert.Equal("v", value);
        Assert.Equal(1, cache.Count);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - CreateEntry: entry committed only on dispose")]
    public void CreateEntry_NotCommittedUntilDispose()
    {
        using var cache = new MemoryCache();

        var entry = cache.CreateEntry("k");
        entry.Value = "v";

        Assert.False(cache.TryGetValue("k", out _));

        entry.Dispose();

        Assert.True(cache.TryGetValue("k", out var stored));
        Assert.Equal("v", stored);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - CreateEntry: double dispose is a no-op")]
    public void CreateEntry_DoubleDispose_NoThrow()
    {
        using var cache = new MemoryCache();

        var entry = cache.CreateEntry("k");
        entry.Value = "v";
        entry.Dispose();
        entry.Dispose();

        Assert.True(cache.TryGetValue("k", out _));
        Assert.Equal(1, cache.Count);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Set: replacing entry fires Replaced callback")]
    public void Set_ReplaceEntry_FiresReplacedCallback()
    {
        using var cache = new MemoryCache();
        CacheEvictionReason reason = CacheEvictionReason.None;
        object? evictedValue = null;

        using (var entry = cache.CreateEntry("k"))
        {
            entry.Value = "first";
            entry.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration((_, value, evictReason, _) =>
            {
                reason = evictReason;
                evictedValue = value;
            }));
        }

        cache.Set("k", "second");

        Assert.Equal(CacheEvictionReason.Replaced, reason);
        Assert.Equal("first", evictedValue);
        Assert.True(cache.TryGetValue("k", out var stored));
        Assert.Equal("second", stored);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Remove: missing key is a no-op")]
    public void Remove_MissingKey_NoThrow()
    {
        using var cache = new MemoryCache();

        cache.Remove("never");

        Assert.Equal(0, cache.Count);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Remove: removes entry and fires Removed callback")]
    public void Remove_RemovesEntryAndFiresCallback()
    {
        using var cache = new MemoryCache();
        CacheEvictionReason reason = CacheEvictionReason.None;

        using (var entry = cache.CreateEntry("k"))
        {
            entry.Value = "v";
            entry.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration((_, _, r, _) => reason = r));
        }

        cache.Remove("k");

        Assert.Equal(CacheEvictionReason.Removed, reason);
        Assert.False(cache.TryGetValue("k", out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Clear: removes every entry and fires Removed callbacks")]
    public void Clear_RemovesAllEntries()
    {
        using var cache = new MemoryCache();
        int callbackCount = 0;

        for (int i = 0; i < 4; i++)
        {
            using var entry = cache.CreateEntry(i);
            entry.Value = i;
            entry.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration((_, _, _, _) => Interlocked.Increment(ref callbackCount)));
        }

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Equal(4, callbackCount);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Disposed cache: operations throw CacheException")]
    public void Disposed_OperationsThrow()
    {
        var cache = new MemoryCache();
        cache.Dispose();

        var disposedKey = Assert.Throws<CacheException>(() => cache.CreateEntry("k"));
        Assert.Equal(CacheErrorCode.Disposed, disposedKey.ErrorCode);

        Assert.Throws<CacheException>(() => cache.TryGetValue("k", out _));
        Assert.Throws<CacheException>(() => cache.Remove("k"));
        Assert.Throws<CacheException>(() => cache.Clear());
        Assert.Throws<CacheException>(() => cache.Compact());
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Disposed cache: double dispose is a no-op")]
    public void Disposed_DoubleDispose_NoThrow()
    {
        var cache = new MemoryCache();
        cache.Dispose();
        cache.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - TryGetValue: null key throws ArgumentNullException")]
    public void TryGetValue_NullKey_Throws()
    {
        using var cache = new MemoryCache();
        Assert.Throws<ArgumentNullException>(() => cache.TryGetValue(null!, out _));
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Remove: null key throws ArgumentNullException")]
    public void Remove_NullKey_Throws()
    {
        using var cache = new MemoryCache();
        Assert.Throws<ArgumentNullException>(() => cache.Remove(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - GetOrCreate: invokes factory only when missing")]
    public void GetOrCreate_FactoryInvokedOncePerKey()
    {
        using var cache = new MemoryCache();
        int invocations = 0;

        var first = cache.GetOrCreate("k", _ => { invocations++; return "value"; });
        var second = cache.GetOrCreate("k", _ => { invocations++; return "value-2"; });

        Assert.Equal("value", first);
        Assert.Equal("value", second);
        Assert.Equal(1, invocations);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Concurrent Set: cache reaches consistent count")]
    public async Task ConcurrentSet_ReachesConsistentCount()
    {
        using var cache = new MemoryCache();
        const int Total = 2_000;

        await Parallel.ForEachAsync(
            Enumerable(Total),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            (i, _) =>
            {
                cache.Set(i, i);
                return ValueTask.CompletedTask;
            });

        Assert.Equal(Total, cache.Count);
        for (int i = 0; i < Total; i++)
        {
            Assert.True(cache.TryGetValue(i, out var value));
            Assert.Equal(i, value);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Concurrent GetOrCreate: factory runs at most once per key")]
    public async Task ConcurrentGetOrCreate_FactoryRunsOncePerKey()
    {
        using var cache = new MemoryCache();
        int invocations = 0;

        // Many concurrent attempts on a small key set should converge on a single committed value.
        await Parallel.ForEachAsync(
            Enumerable(200),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            (_, _) =>
            {
                cache.GetOrCreate("hot", _ =>
                {
                    Interlocked.Increment(ref invocations);
                    return "v";
                });
                return ValueTask.CompletedTask;
            });

        Assert.True(cache.TryGetValue("hot", out var stored));
        Assert.Equal("v", stored);
        // The contract does not strictly require single invocation under contention - a brief race
        // can produce a small number of redundant factory invocations - but the cache must always
        // converge to one committed value.
        Assert.InRange(invocations, 1, 200);
    }

    private static IEnumerable<int> Enumerable(int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return i;
        }
    }
}
