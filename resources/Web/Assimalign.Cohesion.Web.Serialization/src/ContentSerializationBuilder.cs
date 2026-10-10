using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Web.Serialization.Internal;

namespace Assimalign.Cohesion.Web.Serialization;

/// <summary>
/// Composes the content-serialization registry: the request-body readers and response-body writers an
/// application registers. Format packages graft their registration verbs onto this type (the built-in
/// JSON pair registers through <see cref="JsonContentSerializationBuilderExtensions.AddJson"/>).
/// </summary>
/// <remarks>
/// Applications receive one in the
/// <c>builder.Services.AddContentSerialization(serialization => serialization.AddJson(...))</c> callback.
/// That verb is a component integration (<c>Properties/ComponentIntegrations.cs</c>, owner decision 34):
/// it creates the builder, runs the callback, calls <see cref="Build"/>, and registers the resulting
/// <see cref="IHttpContentSerializationFeature"/> as an <c>IHttpFeature</c> singleton. A composition
/// surface without a service container registers <see cref="Build"/>'s result through
/// <c>IWebApplicationBuilder.AddFeature</c>.
/// </remarks>
public sealed class ContentSerializationBuilder
{
    private readonly List<IHttpContentReader> _readers = new();
    private readonly List<IHttpContentWriter> _writers = new();

    /// <summary>
    /// Initializes a builder with no formats registered.
    /// </summary>
    public ContentSerializationBuilder()
    {
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

        _readers.Add(reader);
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

        _writers.Add(writer);
        return this;
    }

    /// <summary>
    /// Builds the registry from the readers and writers registered so far.
    /// </summary>
    /// <remarks>
    /// The registry holds a snapshot of the registrations: formats added after this call do not reach it.
    /// </remarks>
    /// <returns>The registry, an <see cref="IHttpContentSerializationFeature"/> for the application to register.</returns>
    public IHttpContentSerializationFeature Build()
    {
        return new HttpContentSerializationFeature(_readers.ToArray(), _writers.ToArray());
    }
}
