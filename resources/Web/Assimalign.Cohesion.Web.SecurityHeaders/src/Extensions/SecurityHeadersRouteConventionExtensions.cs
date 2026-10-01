using System;

using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.SecurityHeaders;

/// <summary>
/// Endpoint convention verbs that override the security headers for one mapped route or for every route
/// of a group.
/// </summary>
/// <remarks>
/// Each verb appends a <see cref="SecurityHeadersMetadata"/> through
/// <see cref="IRouterConventionBuilder.WithMetadata"/>, composed when the route table is built; the most
/// specific declaration wins. The override applies to every response for a request routed to the
/// endpoint, error responses included, and only where <c>UseSecurityHeaders</c> is registered. Responses
/// that never reach an endpoint keep the pipeline's policy.
/// </remarks>
public static class SecurityHeadersRouteConventionExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IRouterConventionBuilder
    {
        /// <summary>
        /// Adjusts the pipeline's security-headers policy for the route (or every route of the group): the
        /// callback receives a copy of the policy configured on <c>UseSecurityHeaders</c>.
        /// </summary>
        /// <param name="configure">
        /// The adjustment, for example <c>policy =&gt; policy.Framing = FramingPolicy.SameOrigin</c>. It is
        /// cached per endpoint, so it must not depend on the request.
        /// </param>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder WithSecurityHeaders(Action<SecurityHeadersPolicy> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(new SecurityHeadersMetadata(configure));
            return builder;
        }

        /// <summary>
        /// Replaces the pipeline's security-headers policy for the route (or every route of the group).
        /// </summary>
        /// <param name="policy">The policy the endpoint's responses carry. A copy is captured.</param>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="policy"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">An enumeration property of <paramref name="policy"/> holds an undefined value.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder WithSecurityHeaders(SecurityHeadersPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(new SecurityHeadersMetadata(policy));
            return builder;
        }

        /// <summary>
        /// Turns the security headers off for the route (or every route of the group), overriding a broader
        /// declaration. Fields the application sets itself are unaffected.
        /// </summary>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder DisableSecurityHeaders()
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(SecurityHeadersMetadata.Disabled);
            return builder;
        }
    }
}
