using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Serialization;

/// <summary>
/// The request-deserialization half of the content-serialization registry: turns a request body
/// of one of the declared <see cref="MediaTypes"/> into a typed value.
/// </summary>
/// <remarks>
/// <para>
/// Readers are registered at composition time through
/// <see cref="ContentSerializationBuilder.AddReader"/> and resolved per request from the
/// <see cref="IHttpContentSerializationFeature"/> by matching the request's <c>Content-Type</c>
/// against <see cref="MediaTypes"/>. Implementations are shared across concurrent exchanges and
/// must be thread-safe.
/// </para>
/// <para>
/// The contract is deliberately non-generic — <see cref="ReadAsync"/> takes the target
/// <see cref="Type"/> and returns <see cref="object"/> — so the registry can store readers
/// heterogeneously and stay NativeAOT-clean (no generic virtual dispatch). Typed ergonomics come
/// from the <c>ReadContentAsync&lt;T&gt;</c> extension, which forwards <c>typeof(T)</c> and casts.
/// </para>
/// </remarks>
public interface IHttpContentReader
{
    /// <summary>
    /// Gets the media types (or media ranges) this reader can deserialize. Matching uses
    /// <see cref="HttpMediaType.Includes(HttpMediaType)"/> with the request's <c>Content-Type</c>
    /// as the candidate.
    /// </summary>
    IReadOnlyList<HttpMediaType> MediaTypes { get; }

    /// <summary>
    /// Determines whether this reader can produce <paramref name="type"/> — for the built-in JSON
    /// reader, whether the registered resolver carries contract metadata for the type.
    /// </summary>
    /// <param name="type">The target CLR type.</param>
    /// <returns><see langword="true"/> when <see cref="ReadAsync"/> can produce the type.</returns>
    bool CanRead(Type type);

    /// <summary>
    /// Deserializes the request body into an instance of <paramref name="type"/>.
    /// </summary>
    /// <param name="request">The request whose body is read.</param>
    /// <param name="type">The target CLR type.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The deserialized value, which may be <see langword="null"/> for a null payload.</returns>
    /// <exception cref="HttpContentSerializationException">
    /// The reader has no serialization contract for <paramref name="type"/>.
    /// </exception>
    ValueTask<object?> ReadAsync(IHttpRequest request, Type type, CancellationToken cancellationToken = default);
}
