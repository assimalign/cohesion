using System;

using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Caching.Internal;

namespace Assimalign.Cohesion.Web.Caching;

/// <summary>
/// Configures the output-cache middleware that <c>builder.Services.AddOutputCache(cache =&gt; ...)</c>
/// registers and <see cref="OutputCacheExtensions.UseOutputCache(IWebApplicationPipelineBuilder)"/> places.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Options"/> holds the base and named policies, the size caps, and the clock. With no base
/// policy the middleware is opt-in: only endpoints carrying <see cref="OutputCacheMetadata"/> are cached.
/// </para>
/// <para>
/// Without <see cref="UseStore"/> the middleware creates its own <see cref="InMemoryOutputCacheStore"/>,
/// sized from <see cref="OutputCacheOptions.SizeLimit"/> and expiring on
/// <see cref="OutputCacheOptions.TimeProvider"/>, and disposes it when the middleware is disposed: the
/// container that <c>AddOutputCache</c> registers it in does that when the application is disposed.
/// Supply a store to hold the eviction handle in application code or to plug in a distributed backend.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("OutputCacheBuilder")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class OutputCacheBuilder
{
    private IOutputCacheStore? _store;

    /// <summary>
    /// Initializes a builder with the defaults: no base or named policy (opt-in caching), an 8&#8239;MiB
    /// per-response cap, and an in-memory store with a 64&#8239;MiB budget on the system clock.
    /// </summary>
    public OutputCacheBuilder()
    {
    }

    /// <summary>
    /// Gets the options: the base and named policies, the per-response and store size caps, and the clock.
    /// </summary>
    /// <remarks>
    /// The middleware reads its options on every request, so <see cref="Build"/> gives it a copy of these,
    /// policies included. A change made afterwards reaches only a later <see cref="Build"/>.
    /// </remarks>
    public OutputCacheOptions Options { get; } = new();

    /// <summary>
    /// Backs the middleware with <paramref name="store"/> instead of an in-memory store it creates.
    /// </summary>
    /// <remarks>
    /// Hold the same store in application code to evict cached responses by tag, or supply a distributed
    /// backend; nothing else in the pipeline changes. The application owns the store: the middleware
    /// never disposes it, and <see cref="OutputCacheOptions.SizeLimit"/> does not apply to it. A later
    /// call replaces the store an earlier call supplied.
    /// </remarks>
    /// <param name="store">The store cached responses round-trip through.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public OutputCacheBuilder UseStore(IOutputCacheStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        return this;
    }

    /// <summary>
    /// Builds the output-cache middleware from a copy of <see cref="Options"/>, over the store
    /// <see cref="UseStore"/> supplied or a new <see cref="InMemoryOutputCacheStore"/>.
    /// </summary>
    /// <returns>
    /// The middleware, which serves a cacheable GET or HEAD request from the store without running the
    /// endpoint and stores a cacheable response once it completes.
    /// </returns>
    public IWebApplicationMiddleware Build()
    {
        OutputCacheOptions options = Options.Snapshot();

        if (_store is not null)
        {
            return new OutputCacheMiddleware(_store, options);
        }

        InMemoryOutputCacheStore store = new(options.SizeLimit, options.TimeProvider);
        return new OutputCacheMiddleware(store, options, ownsStore: true);
    }

    private sealed class DebugView
    {
        private readonly OutputCacheBuilder _target;

        public DebugView(OutputCacheBuilder value)
        {
            _target = value;
        }

        public OutputCacheOptions Options => _target.Options;
    }
}
