using System;

using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// Endpoint convention verbs that curate an endpoint's API description: tags, a summary, a longer
/// description, and exclusion. Each works on one mapped route or on every route of a group.
/// </summary>
/// <remarks>
/// <para>
/// Each verb appends one neutral description carrier through
/// <see cref="IRouterConventionBuilder.WithMetadata"/>, composed when the route table is built (outer
/// group first, then the route), so the order of calls does not matter. A documentation adapter reads
/// the carriers from the route table; the OpenAPI adapter (<c>Assimalign.Cohesion.Web.OpenApi</c>, #152)
/// maps them onto operation tags, summaries and descriptions, and leaves excluded endpoints out of the
/// document.
/// </para>
/// <para>
/// The verbs live here, beside the source-generated parameter and response descriptions, rather than in
/// the OpenAPI package, so an endpoint can be described without taking a dependency on any one
/// documentation format.
/// </para>
/// </remarks>
public static class EndpointDescriptionConventionExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IRouterConventionBuilder
    {
        /// <summary>
        /// Groups the route, or every route of the group, under <paramref name="tags"/> by attaching an
        /// <see cref="EndpointTagsMetadata"/>. Tags from the route and every group above it all apply.
        /// </summary>
        /// <param name="tags">The tag names. At least one is required.</param>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="tags"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="tags"/> is empty or contains a <see langword="null"/>, empty, or whitespace entry.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder WithTags(params string[] tags)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(new EndpointTagsMetadata(tags));
            return builder;
        }

        /// <summary>
        /// Gives the route, or every route of the group, a short summary by attaching an
        /// <see cref="EndpointSummaryMetadata"/>. The most specific declaration wins.
        /// </summary>
        /// <param name="summary">The summary: one short line.</param>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="summary"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="summary"/> is empty or whitespace.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder WithSummary(string summary)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(new EndpointSummaryMetadata(summary));
            return builder;
        }

        /// <summary>
        /// Gives the route, or every route of the group, a longer description by attaching an
        /// <see cref="EndpointDescriptionMetadata"/>. The most specific declaration wins.
        /// </summary>
        /// <param name="description">The description. Documentation formats such as OpenAPI render it as CommonMark.</param>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="description"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="description"/> is empty or whitespace.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder WithDescription(string description)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(new EndpointDescriptionMetadata(description));
            return builder;
        }

        /// <summary>
        /// Leaves the route, or every route of the group, out of the application's API description by
        /// attaching <see cref="ExcludeFromDescriptionMetadata.Instance"/>. Matching and dispatch are
        /// unaffected.
        /// </summary>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        public TBuilder ExcludeFromDescription()
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(ExcludeFromDescriptionMetadata.Instance);
            return builder;
        }
    }
}
