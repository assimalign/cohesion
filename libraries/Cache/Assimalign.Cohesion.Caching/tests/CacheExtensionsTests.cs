using System;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Caching.Tests;

public class CacheExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Caching] - CacheExtensions.Get: returns null when entry is missing")]
    public void Get_MissingEntry_ReturnsNull()
    {
        var cache = new FakeCache();

        var value = cache.Get("missing");

        Assert.Null(value);
    }

    [Fact(DisplayName = "Cohesion Test [Caching] - CacheExtensions.Get: returns value when entry exists")]
    public void Get_PresentEntry_ReturnsValue()
    {
        var cache = new FakeCache();
        cache.Storage["k"] = "v";

        var value = cache.Get("k");

        Assert.Equal("v", value);
    }

    [Fact(DisplayName = "Cohesion Test [Caching] - CacheExtensions.Get<T>: returns default for incompatible type")]
    public void GetT_IncompatibleType_ReturnsDefault()
    {
        var cache = new FakeCache();
        cache.Storage["k"] = "string-value";

        var value = cache.Get<int>("k");

        Assert.Equal(0, value);
    }

    [Fact(DisplayName = "Cohesion Test [Caching] - CacheExtensions.Get<T>: returns typed value when compatible")]
    public void GetT_CompatibleType_ReturnsValue()
    {
        var cache = new FakeCache();
        cache.Storage["k"] = 42;

        var value = cache.Get<int>("k");

        Assert.Equal(42, value);
    }

    [Fact(DisplayName = "Cohesion Test [Caching] - CacheExtensions.TryGetValue<T>: emits typed value when compatible")]
    public void TryGetValueT_CompatibleType_ReturnsTrue()
    {
        var cache = new FakeCache();
        cache.Storage["k"] = "hello";

        Assert.True(cache.TryGetValue<string>("k", out var value));
        Assert.Equal("hello", value);
    }

    [Fact(DisplayName = "Cohesion Test [Caching] - CacheExtensions.TryGetValue<T>: returns false on missing entry")]
    public void TryGetValueT_MissingEntry_ReturnsFalse()
    {
        var cache = new FakeCache();

        Assert.False(cache.TryGetValue<string>("missing", out var value));
        Assert.Null(value);
    }

    [Fact(DisplayName = "Cohesion Test [Caching] - CacheExtensions.TryGetValue<T>: returns false on incompatible value")]
    public void TryGetValueT_IncompatibleValue_ReturnsFalse()
    {
        var cache = new FakeCache();
        cache.Storage["k"] = 12345;

        Assert.False(cache.TryGetValue<string>("k", out var value));
        Assert.Null(value);
    }

    [Fact(DisplayName = "Cohesion Test [Caching] - CacheExtensions.Set: creates and commits entry")]
    public void Set_CommitsValue()
    {
        var cache = new FakeCache();

        var result = cache.Set("k", "v");

        Assert.Equal("v", result);
        Assert.True(cache.Storage.TryGetValue("k", out var stored));
        Assert.Equal("v", stored);
    }

    [Fact(DisplayName = "Cohesion Test [Caching] - CacheExtensions.Set: absolute expiration overload forwards to entry")]
    public void Set_AbsoluteExpiration_ForwardsToEntry()
    {
        var cache = new FakeCache();
        var expiration = DateTimeOffset.UtcNow.AddMinutes(5);

        cache.Set("k", "v", expiration);

        var entry = cache.LastCommittedEntry;
        Assert.NotNull(entry);
        Assert.Equal(expiration, entry!.AbsoluteExpiration);
    }

    [Fact(DisplayName = "Cohesion Test [Caching] - CacheExtensions.Set: relative expiration overload forwards to entry")]
    public void Set_RelativeExpiration_ForwardsToEntry()
    {
        var cache = new FakeCache();

        cache.Set("k", "v", TimeSpan.FromSeconds(30));

        var entry = cache.LastCommittedEntry;
        Assert.NotNull(entry);
        Assert.Equal(TimeSpan.FromSeconds(30), entry!.AbsoluteExpirationRelativeToNow);
    }

    [Fact(DisplayName = "Cohesion Test [Caching] - CacheExtensions.Set: token expiration overload appends token")]
    public void Set_TokenExpiration_AppendsToken()
    {
        var cache = new FakeCache();
        var token = new FakeChangeToken();

        cache.Set("k", "v", token);

        var entry = cache.LastCommittedEntry;
        Assert.NotNull(entry);
        Assert.Contains(token, entry!.ExpirationTokens);
    }

    [Fact(DisplayName = "Cohesion Test [Caching] - CacheExtensions.GetOrCreate: returns existing value without invoking factory")]
    public void GetOrCreate_ExistingValue_DoesNotInvokeFactory()
    {
        var cache = new FakeCache();
        cache.Storage["k"] = "existing";
        bool factoryRan = false;

        var value = cache.GetOrCreate<string>("k", _ =>
        {
            factoryRan = true;
            return "new";
        });

        Assert.Equal("existing", value);
        Assert.False(factoryRan);
    }

    [Fact(DisplayName = "Cohesion Test [Caching] - CacheExtensions.GetOrCreate: invokes factory and commits value when missing")]
    public void GetOrCreate_Missing_InvokesFactory()
    {
        var cache = new FakeCache();

        var value = cache.GetOrCreate<string>("k", _ => "new");

        Assert.Equal("new", value);
        Assert.True(cache.Storage.TryGetValue("k", out var stored));
        Assert.Equal("new", stored);
    }

    [Fact(DisplayName = "Cohesion Test [Caching] - CacheExtensions: throws on null cache or key")]
    public void NullArguments_Throw()
    {
        ICache cache = new FakeCache();

        Assert.Throws<ArgumentNullException>(() => CacheExtensions.Get(null!, "k"));
        Assert.Throws<ArgumentNullException>(() => cache.Get((object)null!));
        Assert.Throws<ArgumentNullException>(() => CacheExtensions.Get<string>(null!, "k"));
        Assert.Throws<ArgumentNullException>(() => cache.Get<string>((object)null!));
        Assert.Throws<ArgumentNullException>(() => CacheExtensions.TryGetValue<string>(null!, "k", out _));
        Assert.Throws<ArgumentNullException>(() => cache.TryGetValue<string>((object)null!, out _));
        Assert.Throws<ArgumentNullException>(() => CacheExtensions.Set(null!, "k", 1));
        Assert.Throws<ArgumentNullException>(() => cache.Set((object)null!, 1));
        Assert.Throws<ArgumentNullException>(() => CacheExtensions.Set(null!, "k", 1, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentNullException>(() => cache.Set((object)null!, 1, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentNullException>(() => CacheExtensions.Set(null!, "k", 1, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentNullException>(() => cache.Set((object)null!, 1, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentNullException>(() => CacheExtensions.Set(null!, "k", 1, new FakeChangeToken()));
        Assert.Throws<ArgumentNullException>(() => cache.Set((object)null!, 1, new FakeChangeToken()));
        Assert.Throws<ArgumentNullException>(() => cache.Set("k", 1, (IChangeToken)null!));
        Assert.Throws<ArgumentNullException>(() => CacheExtensions.GetOrCreate<int>(null!, "k", _ => 1));
        Assert.Throws<ArgumentNullException>(() => cache.GetOrCreate<int>((object)null!, _ => 1));
        Assert.Throws<ArgumentNullException>(() => cache.GetOrCreate<int>("k", null!));
    }

    private sealed class FakeCache : ICache
    {
        public Dictionary<object, object?> Storage { get; } = new();
        public FakeCacheEntry? LastCommittedEntry { get; private set; }

        public ICacheEntry CreateEntry(object key)
        {
            return new FakeCacheEntry(key, OnCommit);
        }

        public bool TryGetValue(object key, out object? value)
        {
            return Storage.TryGetValue(key, out value);
        }

        public void Remove(object key) => Storage.Remove(key);

        public void Clear() => Storage.Clear();

        public void Dispose()
        {
        }

        private void OnCommit(FakeCacheEntry entry)
        {
            LastCommittedEntry = entry;
            Storage[entry.Key] = entry.Value;
        }
    }

    private sealed class FakeCacheEntry : ICacheEntry
    {
        private readonly Action<FakeCacheEntry> _commit;
        private bool _disposed;

        public FakeCacheEntry(object key, Action<FakeCacheEntry> commit)
        {
            Key = key;
            _commit = commit;
            ExpirationTokens = new List<IChangeToken>();
            PostEvictionCallbacks = new List<PostEvictionCallbackRegistration>();
        }

        public object Key { get; }
        public object? Value { get; set; }
        public DateTimeOffset? AbsoluteExpiration { get; set; }
        public TimeSpan? AbsoluteExpirationRelativeToNow { get; set; }
        public TimeSpan? SlidingExpiration { get; set; }
        public IList<IChangeToken> ExpirationTokens { get; }
        public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks { get; }
        public CacheEntryPriority Priority { get; set; } = CacheEntryPriority.Normal;
        public long? Size { get; set; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _commit(this);
        }
    }

    private sealed class FakeChangeToken : IChangeToken
    {
        public IDisposable OnChange(Action<object?> callback, object? state)
        {
            return new Noop();
        }

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}
