using System.Collections.Generic;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Serialization;

/// <summary>
/// The content-serialization registry, exposed to middleware and handlers as a typed feature on
/// <see cref="IHttpContext.Features"/>: media-type-keyed lookup over the registered
/// request-deserialization (<see cref="IHttpContentReader"/>) and response-serialization
/// (<see cref="IHttpContentWriter"/>) halves.
/// </summary>
/// <remarks>
/// <para>
/// The feature is composed once at builder time (<c>AddContentSerialization</c> /
/// <c>AddJsonSerialization</c>) and seeded onto every exchange, per the area's builder-time-only
/// composition model. The lookup surface is non-throwing — <see langword="null"/> means "nothing
/// registered for that media type", which is how outcome-producing layers (content negotiation,
/// source-generated binding) branch to <c>415</c>/<c>406</c> responses without exceptions. The
/// throwing convenience path is the <c>ReadContentAsync</c>/<c>WriteContentAsync</c> extensions.
/// </para>
/// </remarks>
public interface IHttpContentSerializationFeature : IHttpFeature
{
    /// <summary>
    /// Gets the registered readers, in registration order.
    /// </summary>
    IReadOnlyList<IHttpContentReader> Readers { get; }

    /// <summary>
    /// Gets the registered writers, in registration order. The first writer is the default used
    /// when a call site names no content type.
    /// </summary>
    IReadOnlyList<IHttpContentWriter> Writers { get; }

    /// <summary>
    /// Resolves the reader for a request <c>Content-Type</c>. Among readers whose declared ranges
    /// include <paramref name="mediaType"/>, the most specific range wins; ties resolve to the
    /// earliest registration.
    /// </summary>
    /// <param name="mediaType">The concrete media type of the request body.</param>
    /// <returns>The matching reader, or <see langword="null"/> when none is registered.</returns>
    IHttpContentReader? GetReader(HttpMediaType mediaType);

    /// <summary>
    /// Resolves the writer for a response content type. Among writers whose declared ranges
    /// include <paramref name="mediaType"/>, the most specific range wins; ties resolve to the
    /// earliest registration.
    /// </summary>
    /// <param name="mediaType">The concrete media type to emit.</param>
    /// <returns>The matching writer, or <see langword="null"/> when none is registered.</returns>
    IHttpContentWriter? GetWriter(HttpMediaType mediaType);
}
