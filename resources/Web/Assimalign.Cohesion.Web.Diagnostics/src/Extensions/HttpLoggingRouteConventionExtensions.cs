using System;

using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Diagnostics;

/// <summary>
/// Endpoint convention verb that overrides the HTTP logging field set for one mapped route or for every
/// route of a group.
/// </summary>
/// <remarks>
/// The verb appends an <see cref="HttpLoggingMetadata"/> through
/// <see cref="IRouterConventionBuilder.WithMetadata"/>, composed when the route table is built; the most
/// specific declaration wins. <c>UseHttpLogging</c> reads it after the endpoint ran, so the override
/// applies wherever the middleware is registered relative to <c>UseRouting</c>.
/// </remarks>
public static class HttpLoggingRouteConventionExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IRouterConventionBuilder
    {
        /// <summary>
        /// Logs the route's exchanges (or every group route's) with <paramref name="fields"/> instead of
        /// the configured field set. <see cref="HttpLoggingFields.None"/> silences them, the usual choice
        /// for health probes.
        /// </summary>
        /// <param name="fields">
        /// The fields to emit. Body capture fields only narrow what the configured field set armed; see
        /// <see cref="HttpLoggingMetadata"/>.
        /// </param>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder WithHttpLogging(HttpLoggingFields fields)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(new HttpLoggingMetadata(fields));
            return builder;
        }
    }
}
