using System;
using System.Collections.Generic;
using System.Threading.RateLimiting;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.RateLimiting;

/// <summary>
/// A rate-limiting policy: a <see cref="PartitionedRateLimiter{TResource}"/> keyed on the HTTP
/// exchange plus the number of permits each request acquires from it. Cohesion supplies the
/// composition surface and the AOT-safe partition-key selectors
/// (<see cref="RateLimitPartitionKeys"/>); the limiter algorithm itself is always a BCL
/// <c>System.Threading.RateLimiting</c> primitive (fixed/sliding window, token bucket, concurrency)
/// — this package never reimplements one.
/// </summary>
/// <remarks>
/// <para>
/// A policy is built once, at builder time, and lives for the application lifetime (the middleware
/// holds it; the pipeline exposes no disposal hook, so the accepted posture is process-lifetime —
/// see the package DESIGN.md). Build a policy either directly from a
/// <see cref="PartitionedRateLimiter{TResource}"/> or, more conveniently, with one of the
/// <see cref="Create{TKey}(Func{IHttpContext, RateLimitPartition{TKey}}, int, IEqualityComparer{TKey})"/>
/// factories that wrap the BCL <c>RateLimitPartition.Get*</c> partition factories.
/// </para>
/// <para>
/// A policy is used two ways: as the <see cref="RateLimitingOptions.GlobalPolicy"/> (applied to
/// every request), or as a named policy (<see cref="RateLimitingOptions.AddPolicy"/>) attached to
/// an endpoint through <see cref="RateLimitingMetadata"/>.
/// </para>
/// </remarks>
public sealed class RateLimitingPolicy
{
    /// <summary>
    /// Creates a policy from a prebuilt partitioned limiter.
    /// </summary>
    /// <param name="limiter">
    /// The partitioned rate limiter keyed on the HTTP exchange. Its partitioner selects a partition
    /// (and therefore a limiter) per request; use <see cref="RateLimitPartitionKeys"/> for the
    /// key-selection half.
    /// </param>
    /// <param name="permitCount">The number of permits each request acquires. Defaults to <c>1</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="limiter"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="permitCount"/> is zero or negative.</exception>
    public RateLimitingPolicy(PartitionedRateLimiter<IHttpContext> limiter, int permitCount = 1)
    {
        ArgumentNullException.ThrowIfNull(limiter);

        if (permitCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(permitCount),
                permitCount,
                "The permit count must be a positive integer.");
        }

        Limiter = limiter;
        PermitCount = permitCount;
    }

    /// <summary>
    /// Gets the partitioned rate limiter the policy acquires leases from. Never <see langword="null"/>.
    /// </summary>
    public PartitionedRateLimiter<IHttpContext> Limiter { get; }

    /// <summary>
    /// Gets the number of permits each request acquires from <see cref="Limiter"/>. Always positive.
    /// </summary>
    public int PermitCount { get; }

    /// <summary>
    /// Creates a policy that partitions requests with the supplied partitioner. The partitioner maps
    /// each exchange to a <see cref="RateLimitPartition{TKey}"/> — a partition key plus the BCL
    /// limiter factory for that key (for example
    /// <c>RateLimitPartition.GetFixedWindowLimiter(key, _ =&gt; new FixedWindowRateLimiterOptions { ... })</c>).
    /// </summary>
    /// <typeparam name="TKey">The partition key type; requests with the same key share a limiter.</typeparam>
    /// <param name="partitioner">
    /// Maps an exchange to its partition. Compose the key half from <see cref="RateLimitPartitionKeys"/>
    /// (for example <c>RateLimitPartitionKeys.ClientAddress(context)</c>) so the client-identity keying
    /// respects the forwarded-headers trust model.
    /// </param>
    /// <param name="permitCount">The number of permits each request acquires. Defaults to <c>1</c>.</param>
    /// <param name="equalityComparer">An optional comparer for partition keys, or <see langword="null"/> for the default.</param>
    /// <returns>The composed policy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="partitioner"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="permitCount"/> is zero or negative.</exception>
    public static RateLimitingPolicy Create<TKey>(
        Func<IHttpContext, RateLimitPartition<TKey>> partitioner,
        int permitCount = 1,
        IEqualityComparer<TKey>? equalityComparer = null)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(partitioner);

        return new RateLimitingPolicy(
            PartitionedRateLimiter.Create(partitioner, equalityComparer),
            permitCount);
    }

    /// <summary>
    /// Creates a policy from a partition-key selector and a per-partition limiter factory. This is the
    /// general typed-selector form: <paramref name="partitionKeySelector"/> maps an exchange to a key,
    /// and <paramref name="limiterFactory"/> supplies the BCL limiter each distinct key gets.
    /// </summary>
    /// <typeparam name="TKey">The partition key type; requests with the same key share a limiter.</typeparam>
    /// <param name="partitionKeySelector">
    /// Maps an exchange to its partition key. Use <see cref="RateLimitPartitionKeys.ClientAddress"/> or
    /// <see cref="RateLimitPartitionKeys.Header"/> for the built-in selectors, or supply any AOT-safe
    /// delegate. Do not key on unvalidated client-supplied headers (BCP 38 spoofing) — see the DESIGN.md.
    /// </param>
    /// <param name="limiterFactory">The factory that builds the BCL <see cref="RateLimiter"/> for a distinct key.</param>
    /// <param name="permitCount">The number of permits each request acquires. Defaults to <c>1</c>.</param>
    /// <param name="equalityComparer">An optional comparer for partition keys, or <see langword="null"/> for the default.</param>
    /// <returns>The composed policy.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="partitionKeySelector"/> or <paramref name="limiterFactory"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="permitCount"/> is zero or negative.</exception>
    public static RateLimitingPolicy Create<TKey>(
        Func<IHttpContext, TKey> partitionKeySelector,
        Func<TKey, RateLimiter> limiterFactory,
        int permitCount = 1,
        IEqualityComparer<TKey>? equalityComparer = null)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(partitionKeySelector);
        ArgumentNullException.ThrowIfNull(limiterFactory);

        return Create(
            context => RateLimitPartition.Get(partitionKeySelector(context), limiterFactory),
            permitCount,
            equalityComparer);
    }
}
