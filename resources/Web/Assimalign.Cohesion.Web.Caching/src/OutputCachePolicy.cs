using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Caching;

/// <summary>
/// A server-owned output-caching policy: how long a matched response stays fresh, which request
/// dimensions partition the cache key, whether authenticated responses may be cached, and the tags
/// stored responses carry for bulk invalidation. Policies are composed at builder time — as the
/// <see cref="OutputCacheOptions.BasePolicy"/> applied to every cacheable request, as a named policy
/// (<see cref="OutputCacheOptions.AddPolicy"/>), or inline on an endpoint through
/// <see cref="OutputCacheMetadata"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cache key.</b> Every cached response keys on the request method, scheme, host, and path. The
/// <c>VaryBy*</c> collections add further request dimensions: <see cref="VaryByHeaders"/> folds named
/// request-header values into the key, <see cref="VaryByRouteValues"/> folds matched route values, and
/// <see cref="VaryByQueryKeys"/> selects which query keys participate — empty (the default) folds the
/// <em>entire</em> query string. On top of the policy key, the middleware always honors the stored
/// response's own <c>Vary</c> header (RFC 9111 §4.1), so a compressed or content-negotiated variant is
/// never served to a client that cannot accept it.
/// </para>
/// <para>
/// <b>Authenticated responses.</b> By default a request carrying an <c>Authorization</c> header, or a
/// response carrying <c>Set-Cookie</c>, is not cached — sharing a per-user representation across clients
/// is a data-leak risk. Set <see cref="CacheAuthenticated"/> only when the policy also partitions the
/// key per principal (for example a <see cref="VaryByHeaders"/> entry for the identity header); the
/// caller owns that correctness.
/// </para>
/// </remarks>
public sealed class OutputCachePolicy
{
    private TimeSpan _duration = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets or sets whether the policy is active. A disabled policy caches nothing (equivalent to no
    /// policy). Defaults to <see langword="true"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the freshness lifetime of a cached response — its time-to-live. Must be positive.
    /// Defaults to one minute.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is not positive.</exception>
    public TimeSpan Duration
    {
        get => _duration;
        set
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The cache duration must be positive.");
            }

            _duration = value;
        }
    }

    /// <summary>
    /// Gets or sets whether the effective time-to-live is shortened by the response's own explicit
    /// freshness (<c>Cache-Control: max-age</c>/<c>s-maxage</c> or <c>Expires</c>), and whether the
    /// response's <c>no-store</c>/<c>private</c>/<c>no-cache</c> directives suppress storage. When
    /// <see langword="true"/> (the default) the origin can shorten — but never lengthen — the policy
    /// <see cref="Duration"/>, and RFC 9111 storage directives are obeyed. When <see langword="false"/>
    /// the policy <see cref="Duration"/> is authoritative and those response directives are ignored.
    /// </summary>
    public bool HonorResponseCacheControl { get; set; } = true;

    /// <summary>
    /// Gets or sets whether responses to authenticated requests may be cached. When <see langword="false"/>
    /// (the default) a request with an <c>Authorization</c> header bypasses the cache and a response with
    /// <c>Set-Cookie</c> is not stored.
    /// </summary>
    public bool CacheAuthenticated { get; set; }

    /// <summary>
    /// Gets or sets a per-policy override for the maximum cacheable response-body size in bytes; a larger
    /// body abandons caching and streams through untouched. <see langword="null"/> (the default) uses the
    /// middleware-wide <see cref="OutputCacheOptions.MaximumBodySize"/>.
    /// </summary>
    public long? MaximumBodySize { get; set; }

    /// <summary>
    /// Gets the request-header names whose values partition the cache key.
    /// </summary>
    public IList<string> VaryByHeaders { get; } = new List<string>();

    /// <summary>
    /// Gets the query keys whose values partition the cache key. Empty (the default) folds the entire
    /// query string into the key.
    /// </summary>
    public IList<string> VaryByQueryKeys { get; } = new List<string>();

    /// <summary>
    /// Gets the matched route-value names whose values partition the cache key.
    /// </summary>
    public IList<string> VaryByRouteValues { get; } = new List<string>();

    /// <summary>
    /// Gets the tags applied to every response stored under this policy, for bulk invalidation through
    /// <see cref="IOutputCacheStore.EvictByTagAsync"/> / <see cref="IOutputCacheFeature.EvictByTagAsync"/>.
    /// </summary>
    public IList<string> Tags { get; } = new List<string>();

    /// <summary>
    /// Adds one or more request-header names to <see cref="VaryByHeaders"/>.
    /// </summary>
    /// <param name="headerNames">The request-header names to vary by.</param>
    /// <returns>The same policy, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="headerNames"/> is <see langword="null"/>.</exception>
    public OutputCachePolicy VaryByHeader(params string[] headerNames)
    {
        ArgumentNullException.ThrowIfNull(headerNames);
        foreach (string name in headerNames)
        {
            if (!string.IsNullOrEmpty(name))
            {
                VaryByHeaders.Add(name);
            }
        }

        return this;
    }

    /// <summary>
    /// Adds one or more query keys to <see cref="VaryByQueryKeys"/>, narrowing the key from the whole
    /// query string to the listed keys.
    /// </summary>
    /// <param name="queryKeys">The query keys to vary by.</param>
    /// <returns>The same policy, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="queryKeys"/> is <see langword="null"/>.</exception>
    public OutputCachePolicy VaryByQuery(params string[] queryKeys)
    {
        ArgumentNullException.ThrowIfNull(queryKeys);
        foreach (string key in queryKeys)
        {
            if (!string.IsNullOrEmpty(key))
            {
                VaryByQueryKeys.Add(key);
            }
        }

        return this;
    }

    /// <summary>
    /// Adds one or more matched route-value names to <see cref="VaryByRouteValues"/>.
    /// </summary>
    /// <param name="routeValueNames">The route-value names to vary by.</param>
    /// <returns>The same policy, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routeValueNames"/> is <see langword="null"/>.</exception>
    public OutputCachePolicy VaryByRouteValue(params string[] routeValueNames)
    {
        ArgumentNullException.ThrowIfNull(routeValueNames);
        foreach (string name in routeValueNames)
        {
            if (!string.IsNullOrEmpty(name))
            {
                VaryByRouteValues.Add(name);
            }
        }

        return this;
    }

    /// <summary>
    /// Adds one or more tags to <see cref="Tags"/>.
    /// </summary>
    /// <param name="tags">The tags to apply to stored responses.</param>
    /// <returns>The same policy, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tags"/> is <see langword="null"/>.</exception>
    public OutputCachePolicy Tag(params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        foreach (string tag in tags)
        {
            if (!string.IsNullOrEmpty(tag))
            {
                Tags.Add(tag);
            }
        }

        return this;
    }
}
