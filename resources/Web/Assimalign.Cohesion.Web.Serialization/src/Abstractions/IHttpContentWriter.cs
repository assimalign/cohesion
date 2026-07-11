using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Serialization;

/// <summary>
/// The response-serialization half of the content-serialization registry: writes a typed value
/// to the response body as one of the declared <see cref="MediaTypes"/>.
/// </summary>
/// <remarks>
/// <para>
/// Writers are registered at composition time through
/// <see cref="ContentSerializationBuilder.AddWriter"/> and resolved per request from the
/// <see cref="IHttpContentSerializationFeature"/>. The first entry of <see cref="MediaTypes"/>
/// must be a concrete (wildcard-free) media type — it is the writer's canonical content type,
/// used when a call site does not name one. Implementations are shared across concurrent
/// exchanges and must be thread-safe.
/// </para>
/// <para>
/// The contract is deliberately non-generic — <see cref="WriteAsync"/> takes the declared
/// <see cref="Type"/> and the value as <see cref="object"/> — so the registry can store writers
/// heterogeneously and stay NativeAOT-clean (no generic virtual dispatch). Typed ergonomics come
/// from the <c>WriteContentAsync&lt;T&gt;</c> extension, which forwards <c>typeof(T)</c>.
/// </para>
/// </remarks>
public interface IHttpContentWriter
{
    /// <summary>
    /// Gets the media types (or media ranges) this writer can serialize to. The first entry is
    /// the writer's canonical concrete media type; matching uses
    /// <see cref="HttpMediaType.Includes(HttpMediaType)"/> with the requested content type as the
    /// candidate.
    /// </summary>
    IReadOnlyList<HttpMediaType> MediaTypes { get; }

    /// <summary>
    /// Determines whether this writer can serialize <paramref name="type"/> — for the built-in
    /// JSON writer, whether the registered resolver carries contract metadata for the type.
    /// </summary>
    /// <param name="type">The declared CLR type of the value.</param>
    /// <returns><see langword="true"/> when <see cref="WriteAsync"/> can serialize the type.</returns>
    bool CanWrite(Type type);

    /// <summary>
    /// Serializes <paramref name="value"/> to the response body as <paramref name="contentType"/>,
    /// setting the response <c>Content-Type</c> header. The writer does not touch the status code —
    /// that remains the caller's decision.
    /// </summary>
    /// <param name="response">The response to write to.</param>
    /// <param name="value">The value to serialize, which may be <see langword="null"/>.</param>
    /// <param name="type">The declared CLR type used to resolve the serialization contract.</param>
    /// <param name="contentType">The concrete media type to emit. Must fall within <see cref="MediaTypes"/>.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>A task that completes when the payload has been written to the response body.</returns>
    /// <exception cref="HttpContentSerializationException">
    /// The writer has no serialization contract for <paramref name="type"/>.
    /// </exception>
    Task WriteAsync(IHttpResponse response, object? value, Type type, HttpMediaType contentType, CancellationToken cancellationToken = default);
}
