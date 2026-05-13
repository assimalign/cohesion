using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Assimalign.Cohesion.Caching;

/// <summary>
/// Convenience helpers over <see cref="ICache"/>.
/// </summary>
public static class CacheExtensions
{
    /// <summary>
    /// Returns the value cached under <paramref name="key"/>, or <see langword="null"/> when the entry is missing.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="cache"/> or <paramref name="key"/> is <see langword="null"/>.</exception>
    public static object? Get(this ICache cache, object key)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(key);

        return cache.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// Returns the value cached under <paramref name="key"/> cast to <typeparamref name="TValue"/>,
    /// or the default value when the entry is missing or holds an incompatible value.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="cache"/> or <paramref name="key"/> is <see langword="null"/>.</exception>
    public static TValue? Get<TValue>(this ICache cache, object key)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(key);

        if (cache.TryGetValue(key, out var value) && value is TValue typed)
        {
            return typed;
        }

        return default;
    }

    /// <summary>
    /// Strongly typed counterpart to <see cref="ICache.TryGetValue(object, out object)"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="cache"/> or <paramref name="key"/> is <see langword="null"/>.</exception>
    public static bool TryGetValue<TValue>(this ICache cache, object key, [MaybeNullWhen(false)] out TValue value)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(key);

        if (cache.TryGetValue(key, out var result) && result is TValue typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Stores <paramref name="value"/> at <paramref name="key"/> and returns it.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="cache"/> or <paramref name="key"/> is <see langword="null"/>.</exception>
    public static TValue Set<TValue>(this ICache cache, object key, TValue value)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(key);

        using var entry = cache.CreateEntry(key);
        entry.Value = value;
        return value;
    }

    /// <summary>
    /// Stores <paramref name="value"/> at <paramref name="key"/> with an absolute expiration timestamp.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="cache"/> or <paramref name="key"/> is <see langword="null"/>.</exception>
    public static TValue Set<TValue>(this ICache cache, object key, TValue value, DateTimeOffset absoluteExpiration)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(key);

        using var entry = cache.CreateEntry(key);
        entry.Value = value;
        entry.AbsoluteExpiration = absoluteExpiration;
        return value;
    }

    /// <summary>
    /// Stores <paramref name="value"/> at <paramref name="key"/> with an absolute expiration window
    /// relative to the commit time.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="cache"/> or <paramref name="key"/> is <see langword="null"/>.</exception>
    public static TValue Set<TValue>(this ICache cache, object key, TValue value, TimeSpan absoluteExpirationRelativeToNow)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(key);

        using var entry = cache.CreateEntry(key);
        entry.Value = value;
        entry.AbsoluteExpirationRelativeToNow = absoluteExpirationRelativeToNow;
        return value;
    }

    /// <summary>
    /// Stores <paramref name="value"/> at <paramref name="key"/> with a token-driven expiration.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="cache"/>, <paramref name="key"/>, or <paramref name="expirationToken"/> is <see langword="null"/>.
    /// </exception>
    public static TValue Set<TValue>(this ICache cache, object key, TValue value, IChangeToken expirationToken)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(expirationToken);

        using var entry = cache.CreateEntry(key);
        entry.Value = value;
        entry.ExpirationTokens.Add(expirationToken);
        return value;
    }

    /// <summary>
    /// Returns the cached value for <paramref name="key"/>, or invokes <paramref name="factory"/>
    /// to populate the cache and returns the new value.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="cache"/>, <paramref name="key"/>, or <paramref name="factory"/> is <see langword="null"/>.
    /// </exception>
    public static TValue? GetOrCreate<TValue>(this ICache cache, object key, Func<ICacheEntry, TValue> factory)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);

        if (cache.TryGetValue(key, out var existing) && existing is TValue typed)
        {
            return typed;
        }

        using var entry = cache.CreateEntry(key);
        var value = factory(entry);
        entry.Value = value;
        return value;
    }
}
