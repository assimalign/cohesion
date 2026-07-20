using System;

using Assimalign.Cohesion.Web.Caching.Internal;

namespace Assimalign.Cohesion.Web.Caching;

/// <summary>
/// Pipeline-builder members that add server-owned output caching to a web application.
/// </summary>
/// <remarks>
/// <para>
/// Register <c>UseOutputCache</c> early — ahead of <c>UseResponseCompression</c> and any
/// content-negotiated write so the buffered response captures the fully-encoded bytes and the stored
/// <c>Vary</c> already carries <c>Accept-Encoding</c>/<c>Accept</c> — and ahead of <c>UseRouting</c>
/// (the middleware performs the router's own side-effect-free match to read per-endpoint
/// <see cref="OutputCacheMetadata"/> before deciding, then serves a hit without invoking the endpoint).
/// Composition is dependency-free: options and the store are captured at builder time and no
/// request-time service location occurs.
/// </para>
/// <para>
/// With no store argument the middleware uses an in-process <see cref="InMemoryOutputCacheStore"/> sized
/// from <see cref="OutputCacheOptions.SizeLimit"/>. Supply an <see cref="IOutputCacheStore"/> through the
/// store overload to hold the eviction handle in application code or to plug in a distributed backend;
/// nothing else in the pipeline changes.
/// </para>
/// </remarks>
public static class OutputCacheExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds the output-cache middleware backed by an in-memory store. With no base policy configured
        /// the middleware is opt-in — only endpoints carrying <see cref="OutputCacheMetadata"/> are cached.
        /// </summary>
        /// <param name="configure">An optional callback to configure the base and named policies, size caps, and clock.</param>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseOutputCache(Action<OutputCacheOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            OutputCacheOptions options = new();
            configure?.Invoke(options);

            IOutputCacheStore store = new InMemoryOutputCacheStore(options.SizeLimit, options.TimeProvider);
            return builder.Use(new OutputCacheMiddleware(store, options));
        }

        /// <summary>
        /// Adds the output-cache middleware backed by the supplied store. Hold the same
        /// <see cref="IOutputCacheStore"/> in application code to evict cached responses by tag, or supply
        /// a distributed backend.
        /// </summary>
        /// <param name="store">The backing store cached responses round-trip through.</param>
        /// <param name="configure">An optional callback to configure the base and named policies, size caps, and clock.</param>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="store"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseOutputCache(IOutputCacheStore store, Action<OutputCacheOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(store);

            OutputCacheOptions options = new();
            configure?.Invoke(options);

            return builder.Use(new OutputCacheMiddleware(store, options));
        }
    }
}
