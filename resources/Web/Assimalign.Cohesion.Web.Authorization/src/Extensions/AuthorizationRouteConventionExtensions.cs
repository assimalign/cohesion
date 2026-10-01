using System;

using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Authorization;

/// <summary>
/// Endpoint convention verbs that require authorization for one mapped route or for every route of a
/// group, or that allow anonymous access.
/// </summary>
/// <remarks>
/// <para>
/// Each verb appends an <see cref="AuthorizationMetadata"/> through
/// <see cref="IRouterConventionBuilder.WithMetadata"/>, composed when the route table is built (outer
/// group first, then the route). Unlike most endpoint policies, authorization items do not override
/// each other: every <c>RequireAuthorization</c> on the endpoint applies, and the request must satisfy
/// all of them. <c>AllowAnonymous</c> clears the requirements declared before it, in the groups above
/// it or earlier on its own builder; a requirement declared after it, by a nested group or the route,
/// still applies.
/// </para>
/// <para>
/// A requirement applies only where <c>UseAuthorization</c> is registered after <c>UseRouting</c>; an
/// endpoint that requires authorization fails the request rather than run unauthorized when it is not.
/// </para>
/// </remarks>
public static class AuthorizationRouteConventionExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IRouterConventionBuilder
    {
        /// <summary>
        /// Requires the default policy (<see cref="AuthorizationOptions.DefaultPolicy"/>, an authenticated
        /// user unless reconfigured) for the route, or for every route of the group.
        /// </summary>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder RequireAuthorization()
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(new AuthorizationMetadata());
            return builder;
        }

        /// <summary>
        /// Requires the named policy registered with <c>AuthorizationOptions.AddPolicy</c> for the route,
        /// or for every route of the group.
        /// </summary>
        /// <param name="policyName">The registered policy name, compared ordinal (case-sensitive).</param>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="policyName"/> is <see langword="null"/> or empty.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        /// <remarks>
        /// The name is resolved when the endpoint is first authorized; an unregistered name fails those
        /// requests with an <see cref="InvalidOperationException"/>.
        /// </remarks>
        public TBuilder RequireAuthorization(string policyName)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(new AuthorizationMetadata(policyName));
            return builder;
        }

        /// <summary>
        /// Requires an inline policy for the route, or for every route of the group.
        /// </summary>
        /// <param name="policy">The policy.</param>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="policy"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder RequireAuthorization(AuthorizationPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(new AuthorizationMetadata(policy));
            return builder;
        }

        /// <summary>
        /// Builds an inline policy and requires it for the route, or for every route of the group.
        /// </summary>
        /// <param name="configure">Configures the policy's requirements and authentication schemes.</param>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// The configured policy has no requirement, or the route table has already been built.
        /// </exception>
        public TBuilder RequireAuthorization(Action<AuthorizationPolicyBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);

            AuthorizationPolicyBuilder policy = new();
            configure.Invoke(policy);

            builder.WithMetadata(new AuthorizationMetadata(policy.Build()));
            return builder;
        }

        /// <summary>
        /// Allows anonymous access to the route, or to every route of the group, overriding the
        /// authorization requirements declared above it (an enclosing group's included) and the fallback
        /// policy. A requirement declared after it, by a nested group or the route, still applies.
        /// </summary>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder AllowAnonymous()
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(AuthorizationMetadata.AllowAnonymous);
            return builder;
        }
    }
}
