using System;

using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Cors;

/// <summary>
/// Endpoint convention verbs that select the CORS policy for one mapped route or for every route of a group.
/// </summary>
/// <remarks>
/// Each verb appends a <see cref="CorsMetadata"/> through <see cref="IRouterConventionBuilder.WithMetadata"/>,
/// composed when the route table is built. The most specific declaration wins: a route's verb overrides its
/// group's, whatever order the calls were made in. A policy applies only where <c>UseCors</c> is registered
/// after <c>UseRouting</c>; an endpoint that declares one fails the request rather than run under another
/// policy when it is not.
/// </remarks>
public static class CorsRouteConventionExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IRouterConventionBuilder
    {
        /// <summary>
        /// Applies the named policy registered through <c>CorsOptions.AddPolicy</c> to the route, or to every
        /// route of the group.
        /// </summary>
        /// <param name="policyName">The registered policy name.</param>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="policyName"/> is <see langword="null"/> or empty.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder RequireCors(string policyName)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(new CorsMetadata(policyName));
            return builder;
        }

        /// <summary>
        /// Applies an inline policy to the route, or to every route of the group.
        /// </summary>
        /// <param name="policy">The policy.</param>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="policy"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder RequireCors(CorsPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(new CorsMetadata(policy));
            return builder;
        }

        /// <summary>
        /// Builds an inline policy and applies it to the route, or to every route of the group. The policy is
        /// built and validated here, when the route is mapped.
        /// </summary>
        /// <param name="configure">Configures the policy.</param>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="configure"/> adds an invalid origin, method or header name.</exception>
        /// <exception cref="InvalidOperationException">
        /// The configured policy is invalid (see <see cref="CorsPolicyBuilder.Build"/>), or the route table has
        /// already been built.
        /// </exception>
        public TBuilder RequireCors(Action<CorsPolicyBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);

            CorsPolicyBuilder policy = new();
            configure(policy);

            builder.WithMetadata(new CorsMetadata(policy.Build()));
            return builder;
        }

        /// <summary>
        /// Disables CORS for the route, or for every route of the group, overriding a broader policy and the
        /// default policy: responses carry no CORS headers and preflights go unanswered, so a browser blocks
        /// cross-origin callers.
        /// </summary>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder DisableCors()
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(CorsMetadata.Disabled);
            return builder;
        }
    }
}
