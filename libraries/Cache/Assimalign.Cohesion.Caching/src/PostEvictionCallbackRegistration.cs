using System;

namespace Assimalign.Cohesion.Caching;

/// <summary>
/// Pairs a <see cref="PostEvictionDelegate"/> with optional caller state for registration on
/// an <see cref="ICacheEntry"/>.
/// </summary>
public sealed class PostEvictionCallbackRegistration
{
    /// <summary>
    /// Initializes a new registration.
    /// </summary>
    /// <param name="callback">The callback delegate. Required.</param>
    /// <param name="state">Optional caller state passed back to the callback when it fires.</param>
    /// <exception cref="ArgumentNullException"><paramref name="callback"/> is <see langword="null"/>.</exception>
    public PostEvictionCallbackRegistration(PostEvictionDelegate callback, object? state = null)
    {
        ArgumentNullException.ThrowIfNull(callback);

        EvictionCallback = callback;
        State = state;
    }

    /// <summary>
    /// Gets the delegate invoked after eviction.
    /// </summary>
    public PostEvictionDelegate EvictionCallback { get; }

    /// <summary>
    /// Gets the caller state passed to the callback.
    /// </summary>
    public object? State { get; }
}
