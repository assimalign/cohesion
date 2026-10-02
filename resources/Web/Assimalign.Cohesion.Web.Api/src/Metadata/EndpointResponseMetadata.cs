using System;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// Endpoint metadata that describes one response an endpoint produces: its status code, the CLR type of
/// the value written as its body, and the body's media type when it is fixed.
/// </summary>
/// <remarks>
/// <para>
/// The Web endpoint-binding source generator attaches, to every typed endpoint it maps:
/// </para>
/// <list type="bullet">
/// <item>
/// a <c>200 OK</c> response. Its <see cref="Type"/> is the value the handler returns — the <c>T</c> of
/// <c>Task&lt;T&gt;</c>, <c>ValueTask&lt;T&gt;</c> or <c>Nullable&lt;T&gt;</c> — or <see langword="null"/>
/// for a handler that returns nothing and writes the response itself. Its <see cref="ContentType"/> is
/// <c>text/plain</c> for a <see cref="string"/>, and <see langword="null"/> for any other value, because
/// the content-serialization registry negotiates the media type from the request's <c>Accept</c>;
/// </item>
/// <item>
/// a <c>204 No Content</c> response with no <see cref="Type"/>, when the compiler's nullability analysis says
/// the result may be <see langword="null"/>: a nullable-annotated declared return, a <c>Nullable&lt;T&gt;</c>,
/// or, for an implicitly typed lambda, a returned value whose null-state is maybe-null. Nullable-oblivious
/// code lists no <c>204</c>, though the thunk still answers <c>204</c> for a <see langword="null"/> at run time.
/// </item>
/// </list>
/// <para>
/// Responses form a set, not a last-wins setting: read them with
/// <c>GetOrderedMetadata&lt;EndpointResponseMetadata&gt;()</c>. An application can describe more responses
/// with <c>WithMetadata</c> on the endpoint or its group — a <c>404</c> the handler answers, for example —
/// and they compose with the generated ones, group items first. The failures the binding thunk itself
/// answers (400, 413 and 415 problem details, 406) are not described.
/// </para>
/// <para>
/// <see cref="Type"/> is a <c>typeof(...)</c> value, never found by inspecting members, so a documentation
/// adapter (OpenAPI, #152) can produce the schema from the application's source-generated
/// <c>JsonTypeInfo</c> for that type without reflection. This sealed carrier is the metadata contract;
/// there is deliberately no interface, following the endpoint-metadata family rule in the Web.Routing
/// DESIGN.
/// </para>
/// </remarks>
public sealed class EndpointResponseMetadata
{
    /// <summary>
    /// Creates the description of one response.
    /// </summary>
    /// <param name="statusCode">The response's status code.</param>
    /// <param name="type">
    /// The CLR type of the value written as the body, or <see langword="null"/> when the response carries
    /// no value the endpoint writes: a <c>204</c>, or a handler that writes its own response.
    /// </param>
    /// <param name="contentType">
    /// The body's media type when it is fixed, or <see langword="null"/> when it is negotiated from the
    /// request's <c>Accept</c> (or there is no body).
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="statusCode"/> is not between 100 and 599.</exception>
    /// <exception cref="ArgumentException"><paramref name="contentType"/> is empty or contains a wildcard.</exception>
    public EndpointResponseMetadata(HttpStatusCode statusCode, Type? type = null, HttpMediaType? contentType = null)
    {
        if (statusCode.Value is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode.Value, "The status code must be between 100 and 599.");
        }

        if (contentType is { } mediaType && (mediaType.IsEmpty || mediaType.HasWildcard))
        {
            throw new ArgumentException("A described content type must be a concrete (wildcard-free) media type.", nameof(contentType));
        }

        StatusCode = statusCode;
        Type = type;
        ContentType = contentType;
    }

    /// <summary>
    /// Gets the response's status code.
    /// </summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// Gets the CLR type of the value written as the body, or <see langword="null"/> when the response
    /// carries no value the endpoint writes.
    /// </summary>
    public Type? Type { get; }

    /// <summary>
    /// Gets the body's media type when it is fixed, or <see langword="null"/> when it is negotiated from the
    /// request's <c>Accept</c> or the response has no body.
    /// </summary>
    public HttpMediaType? ContentType { get; }
}
