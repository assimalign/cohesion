using System;

using Assimalign.Cohesion.Web.Serialization.Internal;

namespace Assimalign.Cohesion.Web.Serialization;

/// <summary>
/// The composition surface for the content-serialization registry, returned by
/// <c>AddContentSerialization</c> / <c>AddJsonSerialization</c>. Format packages graft their
/// registration verbs onto this type (the built-in JSON pair registers through
/// <see cref="JsonContentSerializationBuilderExtensions.AddJson"/>).
/// </summary>
/// <remarks>
/// Registration is composition-time only: the builder feeds the feature instance that
/// <c>AddContentSerialization</c> attached to the application, and the feature reads the
/// registrations live, so verbs chained after the root call still take effect. Mutating the
/// registry while the application is serving is not supported.
/// </remarks>
public sealed class ContentSerializationBuilder
{
    private readonly HttpContentSerializationFeature _feature;

    internal ContentSerializationBuilder(HttpContentSerializationFeature feature)
    {
        _feature = feature;
    }

    /// <summary>
    /// Registers a request-body reader. Readers are consulted in registration order; among ranges
    /// that include a request's <c>Content-Type</c>, the most specific registration wins.
    /// </summary>
    /// <param name="reader">The reader to register.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="reader"/> declares no media types.</exception>
    public ContentSerializationBuilder AddReader(IHttpContentReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (reader.MediaTypes is not { Count: > 0 })
        {
            throw new ArgumentException("A content reader must declare at least one media type.", nameof(reader));
        }

        _feature.AddReader(reader);
        return this;
    }

    /// <summary>
    /// Registers a response-body writer. The first registered writer is the default used when a
    /// call site names no content type.
    /// </summary>
    /// <param name="writer">The writer to register.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="writer"/> declares no media types, or its first media type is not concrete
    /// (the first entry is the writer's canonical content type and must be wildcard-free).
    /// </exception>
    public ContentSerializationBuilder AddWriter(IHttpContentWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (writer.MediaTypes is not { Count: > 0 })
        {
            throw new ArgumentException("A content writer must declare at least one media type.", nameof(writer));
        }
        if (writer.MediaTypes[0].HasWildcard)
        {
            throw new ArgumentException(
                "A content writer's first media type is its canonical content type and must be concrete (wildcard-free).",
                nameof(writer));
        }

        _feature.AddWriter(writer);
        return this;
    }
}
