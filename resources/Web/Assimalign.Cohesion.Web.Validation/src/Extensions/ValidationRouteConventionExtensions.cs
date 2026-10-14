using System;

using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Validation;

/// <summary>
/// Endpoint convention verbs that turn request validation off, or on, for one mapped route or for every
/// route of a group.
/// </summary>
/// <remarks>
/// Each verb appends a <see cref="ValidationMetadata"/> through <see cref="IRouterConventionBuilder.WithMetadata"/>,
/// composed when the route table is built. The most specific declaration wins: a route-level item
/// overrides a group-level one, and either overrides the application's default
/// (<see cref="EndpointValidationOptions.Enabled"/>).
/// </remarks>
public static class ValidationRouteConventionExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IRouterConventionBuilder
    {
        /// <summary>
        /// Validates the values bound for the route (or for every route of the group), even when
        /// validation is off by default, and overrides an exemption declared on an enclosing group.
        /// </summary>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder RequireValidation()
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(ValidationMetadata.Required);
            return builder;
        }

        /// <summary>
        /// Exempts the route (or every route of the group) from validation, overriding the application's
        /// default and a requirement declared on an enclosing group.
        /// </summary>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder DisableValidation()
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(ValidationMetadata.Disabled);
            return builder;
        }
    }
}
