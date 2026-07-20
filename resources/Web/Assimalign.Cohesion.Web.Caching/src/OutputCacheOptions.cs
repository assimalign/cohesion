using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Caching;

/// <summary>
/// Builder-time options for the output-cache middleware
/// (<see cref="OutputCacheExtensions.UseOutputCache(IWebApplicationPipelineBuilder, Action{OutputCacheOptions})"/>).
/// Composition is dependency-free: the base and named policies, size caps, and clock are captured at
/// builder time; no service container, configuration binding, or request-time service location occurs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in vs. base policy.</b> With no <see cref="BasePolicy"/> the middleware is opt-in: only
/// endpoints carrying <see cref="OutputCacheMetadata"/> (a named or inline policy, or
/// <see cref="OutputCacheMetadata.Enabled"/>) are cached. Setting a base policy makes it apply to every
/// cacheable request; an endpoint can still override it with its own metadata or opt out with
/// <see cref="OutputCacheMetadata.Disabled"/>.
/// </para>
/// <para>
/// <b>Size caps.</b> <see cref="MaximumBodySize"/> is the per-response cap the buffering middleware
/// enforces (a larger body abandons caching and streams through untouched). <see cref="SizeLimit"/> is
/// the cumulative budget of the default in-memory store, enforced by priority/LRU capacity eviction.
/// Both are ignored for the store when an application supplies its own <see cref="IOutputCacheStore"/>.
/// </para>
/// </remarks>
public sealed class OutputCacheOptions
{
    // Ordinal (case-sensitive) policy names, matching the rate-limiting policy map: a policy name is a
    // developer-chosen key, so exact-match resolution is the least surprising.
    private readonly Dictionary<string, OutputCachePolicy> _policies = new(StringComparer.Ordinal);
    private long _maximumBodySize = 8L * 1024 * 1024;
    private long _sizeLimit = InMemoryOutputCacheStore.DefaultSizeLimit;

    /// <summary>
    /// Gets or sets the policy applied to every cacheable request. <see langword="null"/> (the default)
    /// leaves the middleware opt-in — only endpoints carrying <see cref="OutputCacheMetadata"/> are cached.
    /// </summary>
    public OutputCachePolicy? BasePolicy { get; set; }

    /// <summary>
    /// Gets or sets the maximum cacheable response-body size in bytes. A response whose body exceeds this
    /// cap abandons caching and streams through untouched. Defaults to 8&#8239;MiB. Must be positive.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is not positive.</exception>
    public long MaximumBodySize
    {
        get => _maximumBodySize;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The maximum body size must be positive.");
            }

            _maximumBodySize = value;
        }
    }

    /// <summary>
    /// Gets or sets the cumulative byte budget of the default in-memory store. Ignored when an application
    /// supplies its own <see cref="IOutputCacheStore"/>. Defaults to 64&#8239;MiB. Must be positive.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is not positive.</exception>
    public long SizeLimit
    {
        get => _sizeLimit;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The size limit must be positive.");
            }

            _sizeLimit = value;
        }
    }

    /// <summary>
    /// Gets or sets the clock used for entry expiration and served <c>Age</c> computation.
    /// <see langword="null"/> uses <see cref="System.TimeProvider.System"/>. Threaded into the default
    /// in-memory store so the store's expiration clock and the served <c>Age</c> agree.
    /// </summary>
    public TimeProvider? TimeProvider { get; set; }

    /// <summary>
    /// Configures the <see cref="BasePolicy"/> applied to every cacheable request, creating it if unset.
    /// </summary>
    /// <param name="configure">A callback that mutates the base policy.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    public OutputCacheOptions AddBasePolicy(Action<OutputCachePolicy> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        BasePolicy ??= new OutputCachePolicy();
        configure(BasePolicy);
        return this;
    }

    /// <summary>
    /// Registers a named policy that endpoints reference through <see cref="OutputCacheMetadata"/>.
    /// </summary>
    /// <param name="name">The policy name (compared with ordinal, case-sensitive semantics). Must be unique.</param>
    /// <param name="configure">A callback that mutates the new policy.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A policy with the same name is already registered.</exception>
    public OutputCacheOptions AddPolicy(string name, Action<OutputCachePolicy> configure)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(configure);

        OutputCachePolicy policy = new();
        configure(policy);

        if (!_policies.TryAdd(name, policy))
        {
            throw new InvalidOperationException($"An output cache policy named '{name}' is already registered.");
        }

        return this;
    }

    /// <summary>
    /// Resolves a named policy registered through <see cref="AddPolicy"/>.
    /// </summary>
    internal bool TryGetPolicy(string name, out OutputCachePolicy? policy) => _policies.TryGetValue(name, out policy);
}
