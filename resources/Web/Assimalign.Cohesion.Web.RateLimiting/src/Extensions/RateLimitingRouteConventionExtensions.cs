using System;

using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.RateLimiting;

/// <summary>
/// Endpoint convention verbs that attach a rate-limiting policy to one mapped route or to every route
/// of a group.
/// </summary>
/// <remarks>
/// Each verb appends a <see cref="RateLimitingMetadata"/> through
/// <see cref="IRouterConventionBuilder.WithMetadata"/>, composed when the route table is built. The
/// most specific declaration wins (a route-level item overrides a group-level one). The policy applies
/// only where <c>UseRateLimiting</c> is registered after <c>UseRouting</c>; an endpoint that requires a
/// policy fails the request rather than run unlimited when it is not.
/// </remarks>
public static class RateLimitingRouteConventionExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IRouterConventionBuilder
    {
        /// <summary>
        /// Limits the route (or every route of the group) with the named policy registered through
        /// <c>RateLimitingOptions.AddPolicy</c>.
        /// </summary>
        /// <param name="policyName">The registered policy name.</param>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="policyName"/> is <see langword="null"/> or empty.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder RequireRateLimiting(string policyName)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(new RateLimitingMetadata(policyName));
            return builder;
        }

        /// <summary>
        /// Limits the route (or every route of the group) with an inline policy.
        /// </summary>
        /// <param name="policy">The policy.</param>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="policy"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder RequireRateLimiting(RateLimitingPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(new RateLimitingMetadata(policy));
            return builder;
        }

        /// <summary>
        /// Exempts the route (or every route of the group) from endpoint rate limiting, overriding a
        /// broader policy. The global limiter still applies.
        /// </summary>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder DisableRateLimiting()
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(RateLimitingMetadata.Disabled);
            return builder;
        }
    }
}
