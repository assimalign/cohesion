using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Results.Internal;

/// <summary>
/// A machine-readable error payload as defined by RFC 9457 (<c>Problem Details for HTTP APIs</c>,
/// which obsoletes RFC 7807). Serialized as <c>application/problem+json</c> by the
/// <see cref="IProblemDetailsWriter"/> family.
/// </summary>
/// <remarks>
/// <para>
/// The five standard members (<see cref="Type"/>, <see cref="Title"/>, <see cref="Status"/>,
/// <see cref="Detail"/>, <see cref="Instance"/>) are all optional per §3.1. When <see cref="Type"/>
/// is omitted it serializes as the reserved default <c>"about:blank"</c>, in which case
/// <see cref="Title"/> SHOULD be the HTTP status phrase (§4.2). Additional, problem-type-specific
/// members go in <see cref="Extensions"/>.
/// </para>
/// <para>
/// This is a plain, mutable value model with no serialization coupling: the writer walks the five
/// members and the extension bag explicitly, so the type carries no <c>[JsonPropertyName]</c>
/// attributes and needs no reflection-based (de)serializer. Extension values are constrained to an
/// AOT-safe set of JSON-shaped types &#8212; see <see cref="Extensions"/>.
/// </para>
/// </remarks>
public sealed class ProblemDetails
{
    /// <summary>
    /// Gets or sets the problem type, a URI reference (RFC 9457 §3.1.1) identifying the problem
    /// category. When <see langword="null"/>, the writer emits the reserved default
    /// <c>"about:blank"</c>.
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// Gets or sets a short, human-readable summary of the problem type (RFC 9457 §3.1.2). For the
    /// default <c>"about:blank"</c> type this SHOULD be the HTTP status phrase.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the HTTP status code generated for this occurrence of the problem
    /// (RFC 9457 §3.1.3).
    /// </summary>
    public int? Status { get; set; }

    /// <summary>
    /// Gets or sets a human-readable explanation specific to this occurrence of the problem
    /// (RFC 9457 §3.1.4).
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>
    /// Gets or sets a URI reference that identifies the specific occurrence of the problem
    /// (RFC 9457 §3.1.5).
    /// </summary>
    public string? Instance { get; set; }

    /// <summary>
    /// Gets the problem-type-specific extension members (RFC 9457 §3.2). Values are constrained to
    /// an AOT-safe, JSON-shaped set &#8212; <see langword="null"/>, <see cref="bool"/>, the CLR
    /// numeric types, <see cref="string"/>, nested string-keyed maps
    /// (<see cref="IEnumerable{T}"/> of <see cref="KeyValuePair{TKey, TValue}"/>), and sequences
    /// (<see cref="System.Collections.IEnumerable"/>) of the same &#8212; so the writer can render
    /// them without reflection. Any other value is rendered as its <see cref="object.ToString"/>
    /// form. Keys that collide with the five standard members are ignored by the writer.
    /// </summary>
    public IDictionary<string, object?> Extensions { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// Creates a <see cref="ProblemDetails"/> for an HTTP status code with the default
    /// <c>"about:blank"</c> type and the status phrase as the title.
    /// </summary>
    /// <param name="statusCode">The HTTP status code the problem represents.</param>
    /// <param name="detail">An optional occurrence-specific explanation.</param>
    /// <returns>A populated <see cref="ProblemDetails"/> instance.</returns>
    public static ProblemDetails FromStatus(HttpStatusCode statusCode, string? detail = null)
    {
        return new ProblemDetails
        {
            Type = ProblemDetailsDefaults.DefaultType,
            Title = ProblemDetailsDefaults.GetTitle(statusCode.Value),
            Status = statusCode.Value,
            Detail = detail
        };
    }
}
