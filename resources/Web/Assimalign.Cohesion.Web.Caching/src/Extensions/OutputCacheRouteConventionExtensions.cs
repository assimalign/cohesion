using System;

using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Caching;

/// <summary>
/// Endpoint convention verbs that attach an output-cache policy to one mapped route or to every route
/// of a group.
/// </summary>
/// <remarks>
/// Each verb appends an <see cref="OutputCacheMetadata"/> through
/// <see cref="IRouterConventionBuilder.WithMetadata"/>, composed when the route table is built; the
/// most specific declaration wins. <c>UseOutputCache</c> applies it when registered after
/// <c>UseRouting</c>. Output caching is optional: an endpoint dispatched without the middleware is
/// simply not cached.
/// </remarks>
public static class OutputCacheRouteConventionExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IRouterConventionBuilder
    {
        /// <summary>
        /// Opts the route (or every route of the group) into output caching under the base policy.
        /// </summary>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder CacheOutput()
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(OutputCacheMetadata.Enabled);
            return builder;
        }

        /// <summary>
        /// Caches the route's output (or every group route's) under the named policy registered on the
        /// output-cache options.
        /// </summary>
        /// <param name="policyName">The registered policy name.</param>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="policyName"/> is <see langword="null"/> or empty.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder CacheOutput(string policyName)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(new OutputCacheMetadata(policyName));
            return builder;
        }

        /// <summary>
        /// Caches the route's output (or every group route's) under an inline policy.
        /// </summary>
        /// <param name="policy">The policy.</param>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="policy"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder CacheOutput(OutputCachePolicy policy)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(new OutputCacheMetadata(policy));
            return builder;
        }

        /// <summary>
        /// Excludes the route (or every route of the group) from output caching, overriding a broader
        /// policy.
        /// </summary>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder DisableOutputCache()
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(OutputCacheMetadata.Disabled);
            return builder;
        }
    }
}
