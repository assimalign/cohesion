using System;

using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Antiforgery;

/// <summary>
/// Endpoint convention verbs that require antiforgery validation on one mapped route or on every route of
/// a group, or exempt a route from it.
/// </summary>
/// <remarks>
/// Each verb appends an <see cref="AntiforgeryMetadata"/> through
/// <see cref="IRouterConventionBuilder.WithMetadata"/>, composed when the route table is built. The most
/// specific declaration wins: a route-level item overrides a group-level one. Validation applies only
/// where <c>UseAntiforgery</c> is registered after <c>UseRouting</c>; an endpoint that requires it fails
/// the request rather than run unprotected when it is not.
/// </remarks>
public static class AntiforgeryRouteConventionExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IRouterConventionBuilder
    {
        /// <summary>
        /// Requires a valid antiforgery token pair on unsafe-method requests to the route (or to every route
        /// of the group).
        /// </summary>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder RequireAntiforgery()
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(AntiforgeryMetadata.Required);
            return builder;
        }

        /// <summary>
        /// Exempts the route (or every route of the group) from antiforgery validation, overriding a
        /// requirement declared on an enclosing group.
        /// </summary>
        /// <remarks>
        /// A source-generated form-bound endpoint requires validation through route-level metadata, which a
        /// group-level exemption does not override: exempt such an endpoint on its own route builder.
        /// </remarks>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder DisableAntiforgery()
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(AntiforgeryMetadata.Disabled);
            return builder;
        }
    }
}
