using System;

using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.RequestTimeouts;

/// <summary>
/// Endpoint convention verbs that attach a request-timeout policy to one mapped route or to every
/// route of a group.
/// </summary>
/// <remarks>
/// Each verb appends a <see cref="RequestTimeoutMetadata"/> through
/// <see cref="IRouterConventionBuilder.WithMetadata"/>, composed when the route table is built; the
/// most specific declaration wins. The policy applies only where <c>UseRequestTimeouts</c> is
/// registered after <c>UseRouting</c>; an endpoint that requires a timeout fails the request rather
/// than run without it when it is not.
/// </remarks>
public static class RequestTimeoutRouteConventionExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IRouterConventionBuilder
    {
        /// <summary>
        /// Bounds the route (or every route of the group) by <paramref name="timeout"/>.
        /// </summary>
        /// <param name="timeout">The timeout.</param>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is zero or negative.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder WithRequestTimeout(TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(new RequestTimeoutMetadata(timeout));
            return builder;
        }

        /// <summary>
        /// Bounds the route (or every route of the group) with <paramref name="policy"/>.
        /// </summary>
        /// <param name="policy">The timeout policy.</param>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="policy"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder WithRequestTimeout(RequestTimeoutPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(new RequestTimeoutMetadata(policy));
            return builder;
        }

        /// <summary>
        /// Exempts the route (or every route of the group) from request timeouts, including the
        /// application default.
        /// </summary>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder DisableRequestTimeout()
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(RequestTimeoutMetadata.Disabled);
            return builder;
        }
    }
}
