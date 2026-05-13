using System;
using System.Collections.Generic;
using System.Threading;
using Assimalign.Cohesion.Caching;
using Assimalign.Cohesion.Caching.InMemory;

namespace Assimalign.Cohesion.Caching.InMemory.Tests;

public class MemoryCacheLifecycleTests
{
    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Absolute expiration evicts entry on next read")]
    public void AbsoluteExpiration_EvictsOnRead()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var cache = new MemoryCache(new MemoryCacheOptions { TimeProvider = time });
        CacheEvictionReason reason = CacheEvictionReason.None;

        using (var entry = cache.CreateEntry("k"))
        {
            entry.Value = "v";
            entry.AbsoluteExpiration = time.GetUtcNow().AddMilliseconds(10);
            entry.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration((_, _, r, _) => reason = r));
        }

        // Within window - present.
        Assert.True(cache.TryGetValue("k", out _));

        // After window - absent + Expired callback.
        time.Advance(TimeSpan.FromMilliseconds(20));

        Assert.False(cache.TryGetValue("k", out _));
        Assert.Equal(CacheEvictionReason.Expired, reason);
        Assert.Equal(0, cache.Count);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - AbsoluteExpirationRelativeToNow honored")]
    public void AbsoluteExpirationRelativeToNow_Honored()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var cache = new MemoryCache(new MemoryCacheOptions { TimeProvider = time });

        using (var entry = cache.CreateEntry("k"))
        {
            entry.Value = "v";
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
        }

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.True(cache.TryGetValue("k", out _));

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(cache.TryGetValue("k", out _));
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Absolute vs relative: earlier wins")]
    public void AbsoluteAndRelative_EarlierWins()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var cache = new MemoryCache(new MemoryCacheOptions { TimeProvider = time });

        // Absolute is at +1h, relative is +1m. Relative wins.
        using (var entry = cache.CreateEntry("k"))
        {
            entry.Value = "v";
            entry.AbsoluteExpiration = time.GetUtcNow().AddHours(1);
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
        }

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(cache.TryGetValue("k", out _));
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Sliding expiration resets on access")]
    public void SlidingExpiration_ResetsOnAccess()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var cache = new MemoryCache(new MemoryCacheOptions { TimeProvider = time });

        using (var entry = cache.CreateEntry("k"))
        {
            entry.Value = "v";
            entry.SlidingExpiration = TimeSpan.FromMinutes(5);
        }

        // Access just inside the window resets the timer.
        time.Advance(TimeSpan.FromMinutes(4));
        Assert.True(cache.TryGetValue("k", out _));

        time.Advance(TimeSpan.FromMinutes(4));
        Assert.True(cache.TryGetValue("k", out _));

        // Going idle past the window evicts.
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.False(cache.TryGetValue("k", out _));
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Sliding never extends past absolute expiration")]
    public void SlidingCappedByAbsolute()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var cache = new MemoryCache(new MemoryCacheOptions { TimeProvider = time });

        using (var entry = cache.CreateEntry("k"))
        {
            entry.Value = "v";
            entry.AbsoluteExpiration = time.GetUtcNow().AddMinutes(10);
            entry.SlidingExpiration = TimeSpan.FromMinutes(5);
        }

        // Polling every 3 minutes keeps the sliding window alive, but at the next access
        // beyond the 10-minute absolute deadline the entry MUST be evicted regardless.
        for (int i = 1; i <= 3; i++)
        {
            time.Advance(TimeSpan.FromMinutes(3));
            Assert.True(cache.TryGetValue("k", out _));
        }

        // Now at t = 9m. Step past absolute (10m).
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(cache.TryGetValue("k", out _));
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Token expiration evicts entry and fires TokenExpired callback")]
    public void TokenExpiration_EvictsEntry()
    {
        var token = new ManualChangeToken();
        using var cache = new MemoryCache();
        CacheEvictionReason reason = CacheEvictionReason.None;

        using (var entry = cache.CreateEntry("k"))
        {
            entry.Value = "v";
            entry.ExpirationTokens.Add(token);
            entry.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration((_, _, r, _) => reason = r));
        }

        Assert.True(cache.TryGetValue("k", out _));

        token.Notify();

        Assert.False(cache.TryGetValue("k", out _));
        Assert.Equal(CacheEvictionReason.TokenExpired, reason);
        Assert.Equal(0, token.ActiveSubscribers);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - InvalidEntry: relative expiration <= 0 throws")]
    public void RelativeExpirationZero_Throws()
    {
        using var cache = new MemoryCache();

        var entry = cache.CreateEntry("k");
        entry.Value = "v";
        entry.AbsoluteExpirationRelativeToNow = TimeSpan.Zero;

        var ex = Assert.Throws<CacheException>(() => entry.Dispose());
        Assert.Equal(CacheErrorCode.InvalidEntry, ex.ErrorCode);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - InvalidEntry: sliding expiration <= 0 throws")]
    public void SlidingExpirationZero_Throws()
    {
        using var cache = new MemoryCache();

        var entry = cache.CreateEntry("k");
        entry.Value = "v";
        entry.SlidingExpiration = TimeSpan.Zero;

        var ex = Assert.Throws<CacheException>(() => entry.Dispose());
        Assert.Equal(CacheErrorCode.InvalidEntry, ex.ErrorCode);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - InvalidEntry: negative size throws")]
    public void NegativeSize_Throws()
    {
        using var cache = new MemoryCache();

        var entry = cache.CreateEntry("k");
        entry.Value = "v";
        entry.Size = -1;

        var ex = Assert.Throws<CacheException>(() => entry.Dispose());
        Assert.Equal(CacheErrorCode.InvalidEntry, ex.ErrorCode);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - SizeLimit: missing size on commit throws InvalidEntry")]
    public void SizeLimit_MissingSize_Throws()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10 });

        var entry = cache.CreateEntry("k");
        entry.Value = "v";

        var ex = Assert.Throws<CacheException>(() => entry.Dispose());
        Assert.Equal(CacheErrorCode.InvalidEntry, ex.ErrorCode);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - SizeLimit: entry larger than limit is rejected")]
    public void SizeLimit_OverlySizedEntry_Rejected()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10 });
        CacheEvictionReason reason = CacheEvictionReason.None;

        var entry = cache.CreateEntry("big");
        entry.Value = "v";
        entry.Size = 100;
        entry.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration((_, _, r, _) => reason = r));

        var ex = Assert.Throws<CacheException>(() => entry.Dispose());
        Assert.Equal(CacheErrorCode.CapacityExceeded, ex.ErrorCode);
        Assert.Equal(CacheEvictionReason.Capacity, reason);
        Assert.False(cache.TryGetValue("big", out _));
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - SizeLimit: capacity eviction targets lower priority first")]
    public void SizeLimit_CapacityEvictionRespectsPriority()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 100,
            CompactionPercentage = 0.5,
        });

        var evictedKeys = new List<object>();
        PostEvictionDelegate trackEviction = (key, _, reason, _) =>
        {
            if (reason == CacheEvictionReason.Capacity)
            {
                lock (evictedKeys)
                {
                    evictedKeys.Add(key);
                }
            }
        };

        // Fill to the brink with mixed priorities. 4 entries of size 25 each fits exactly.
        foreach (var (key, priority) in new[]
        {
            ("low", CacheEntryPriority.Low),
            ("normal", CacheEntryPriority.Normal),
            ("high", CacheEntryPriority.High),
            ("never", CacheEntryPriority.NeverRemove),
        })
        {
            using var entry = cache.CreateEntry(key);
            entry.Value = key;
            entry.Size = 25;
            entry.Priority = priority;
            entry.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration(trackEviction));
        }

        Assert.Equal(100, cache.TotalSize);

        // Now add an entry that forces compaction. Should evict the Low first.
        using (var bump = cache.CreateEntry("bump"))
        {
            bump.Value = "bump";
            bump.Size = 25;
            bump.Priority = CacheEntryPriority.Normal;
        }

        // After compaction we should be at or below the target (limit - limit*0.5 = 50). The Low
        // entry must be among those evicted; NeverRemove must not be.
        Assert.Contains("low", evictedKeys);
        Assert.DoesNotContain("never", evictedKeys);
        Assert.True(cache.TotalSize <= 100);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - SizeLimit: TotalSize stays zero when limit unset")]
    public void SizeLimit_Unset_TotalSizeStaysZero()
    {
        using var cache = new MemoryCache();

        cache.Set("k", "v");

        Assert.Equal(0, cache.TotalSize);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Compact: forces expiration scan")]
    public void Compact_ScansForExpired()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var cache = new MemoryCache(new MemoryCacheOptions
        {
            TimeProvider = time,
            ExpirationScanFrequency = TimeSpan.FromHours(1),
        });

        for (int i = 0; i < 5; i++)
        {
            using var entry = cache.CreateEntry(i);
            entry.Value = i;
            entry.AbsoluteExpiration = time.GetUtcNow().AddSeconds(1);
        }

        time.Advance(TimeSpan.FromSeconds(2));

        cache.Compact();

        Assert.Equal(0, cache.Count);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - ExpirationScanFrequency: scan runs eventually on access")]
    public void ExpirationScan_RunsEventually()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var cache = new MemoryCache(new MemoryCacheOptions
        {
            TimeProvider = time,
            ExpirationScanFrequency = TimeSpan.FromSeconds(1),
        });

        for (int i = 0; i < 5; i++)
        {
            using var entry = cache.CreateEntry(i);
            entry.Value = i;
            entry.AbsoluteExpiration = time.GetUtcNow().AddSeconds(1);
        }

        time.Advance(TimeSpan.FromSeconds(2));

        // Read of an unrelated (never-existed) key still triggers the scan.
        cache.TryGetValue("trigger", out _);

        Assert.Equal(0, cache.Count);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Callback throw does not block other callbacks")]
    public void CallbackException_DoesNotBlockOthers()
    {
        using var cache = new MemoryCache();
        bool secondFired = false;

        using (var entry = cache.CreateEntry("k"))
        {
            entry.Value = "v";
            entry.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration((_, _, _, _) => throw new InvalidOperationException("boom")));
            entry.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration((_, _, _, _) => secondFired = true));
        }

        cache.Remove("k");

        Assert.True(secondFired);
    }

    [Fact(DisplayName = "Cohesion Test [Caching.InMemory] - Cache dispose releases all entries with Removed reason")]
    public void Dispose_ReleasesAllEntries()
    {
        var cache = new MemoryCache();
        int callbackCount = 0;

        for (int i = 0; i < 3; i++)
        {
            using var entry = cache.CreateEntry(i);
            entry.Value = i;
            entry.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration((_, _, r, _) =>
            {
                if (r == CacheEvictionReason.Removed)
                {
                    Interlocked.Increment(ref callbackCount);
                }
            }));
        }

        cache.Dispose();

        Assert.Equal(3, callbackCount);
    }

}
