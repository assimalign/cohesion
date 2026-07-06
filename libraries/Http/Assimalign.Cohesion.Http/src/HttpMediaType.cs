using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A parsed media type or media range (RFC 9110 &#167; 8.3.1, &#167; 12.5.1): a
/// <c>type "/" subtype</c> pair with optional parameters. Supports the structured-syntax
/// suffix convention (<c>application/vnd.api+json</c> &#8594; suffix <c>json</c>),
/// wildcards for media ranges (<c>*/*</c>, <c>text/*</c>), case-insensitive matching, and a
/// specificity score for RFC 9110 &#167; 12.5.1 precedence ordering.
/// </summary>
/// <remarks>
/// <para>
/// A media type is the value of a <c>Content-Type</c> header; a media range is an entry in an
/// <c>Accept</c> header, which additionally carries a quality weight. The <c>q</c> weight is
/// <em>not</em> a media-type parameter — it belongs to the Accept grammar and is separated by
/// <see cref="HttpAcceptParser"/>, so it never appears in <see cref="Parameters"/>.
/// </para>
/// <para>
/// Matching is directional: treat the instance as a (possibly wildcard) range and call
/// <see cref="Includes(HttpMediaType)"/> to test whether a concrete media type falls within it.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly struct HttpMediaType : IEquatable<HttpMediaType>
{
    private readonly HttpMediaTypeParameter[]? parameters;

    /// <summary>The <c>*/*</c> media range that matches every media type.</summary>
    public static HttpMediaType Any { get; } = new("*", "*", null);

    /// <summary>The <c>application/json</c> media type.</summary>
    public static HttpMediaType ApplicationJson { get; } = new("application", "json", null);

    /// <summary>The <c>application/xml</c> media type.</summary>
    public static HttpMediaType ApplicationXml { get; } = new("application", "xml", null);

    /// <summary>The <c>application/octet-stream</c> media type.</summary>
    public static HttpMediaType ApplicationOctetStream { get; } = new("application", "octet-stream", null);

    /// <summary>The <c>application/x-www-form-urlencoded</c> media type.</summary>
    public static HttpMediaType FormUrlEncoded { get; } = new("application", "x-www-form-urlencoded", null);

    /// <summary>The <c>multipart/form-data</c> media type.</summary>
    public static HttpMediaType MultipartFormData { get; } = new("multipart", "form-data", null);

    /// <summary>The <c>text/plain</c> media type.</summary>
    public static HttpMediaType TextPlain { get; } = new("text", "plain", null);

    /// <summary>The <c>text/html</c> media type.</summary>
    public static HttpMediaType TextHtml { get; } = new("text", "html", null);

    private HttpMediaType(string type, string subType, HttpMediaTypeParameter[]? parameters)
    {
        Type = type;
        SubType = subType;
        this.parameters = parameters is { Length: > 0 } ? parameters : null;
    }

    /// <summary>
    /// Initializes a media type by parsing <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The media type text (e.g. <c>text/html; charset=utf-8</c>).</param>
    /// <exception cref="HttpException">The value is not a well-formed media type.</exception>
    public HttpMediaType(string value)
    {
        HttpMediaType parsed = Parse(value);
        Type = parsed.Type;
        SubType = parsed.SubType;
        parameters = parsed.parameters;
    }

    /// <summary>
    /// Gets the top-level type, lower-cased (e.g. <c>text</c>), or <c>*</c> for a wildcard range.
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// Gets the subtype, lower-cased and including any structured-syntax suffix
    /// (e.g. <c>vnd.api+json</c>), or <c>*</c> for a wildcard range.
    /// </summary>
    public string SubType { get; }

    /// <summary>
    /// Gets the structured-syntax suffix without its leading <c>+</c> (e.g. <c>json</c> for
    /// <c>application/vnd.api+json</c>), or an empty string when the subtype has no suffix.
    /// </summary>
    public string Suffix
    {
        get
        {
            int plus = SubType.LastIndexOf('+');
            return plus > 0 && plus < SubType.Length - 1 ? SubType[(plus + 1)..] : string.Empty;
        }
    }

    /// <summary>
    /// Gets the media-type parameters (e.g. <c>charset</c>, <c>boundary</c>), never
    /// including the Accept <c>q</c> weight. Empty when the media type has no parameters.
    /// </summary>
    public IReadOnlyList<HttpMediaTypeParameter> Parameters
        => parameters ?? (IReadOnlyList<HttpMediaTypeParameter>)Array.Empty<HttpMediaTypeParameter>();

    /// <summary>
    /// Gets the value of the <c>charset</c> parameter, or <see langword="null"/> when absent.
    /// </summary>
    public string? Charset => TryGetParameter("charset", out string? value) ? value : null;

    /// <summary>Gets a value indicating whether the top-level type is the wildcard <c>*</c>.</summary>
    public bool IsWildcardType => Type == "*";

    /// <summary>Gets a value indicating whether the subtype is the wildcard <c>*</c>.</summary>
    public bool IsWildcardSubType => SubType == "*";

    /// <summary>Gets a value indicating whether either the type or the subtype is a wildcard.</summary>
    public bool HasWildcard => IsWildcardType || IsWildcardSubType;

    /// <summary>Gets a value indicating whether this instance was default-constructed (never parsed).</summary>
    public bool IsEmpty => Type is null;

    /// <summary>
    /// Gets the RFC 9110 &#167; 12.5.1 specificity used to order media ranges: <c>*/*</c> is
    /// least specific, then <c>type/*</c>, then a concrete <c>type/subtype</c>, with each
    /// additional parameter increasing specificity further.
    /// </summary>
    public int Specificity
    {
        get
        {
            if (IsWildcardType)
            {
                return 0;
            }
            if (IsWildcardSubType)
            {
                return 1;
            }
            return 2 + (parameters?.Length ?? 0);
        }
    }

    private string DebuggerDisplay => IsEmpty ? "<empty>" : ToString();

    /// <summary>
    /// Attempts to look up a parameter value by name (case-insensitive).
    /// </summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="value">When this method returns <see langword="true"/>, the parameter value.</param>
    /// <returns><see langword="true"/> when a parameter with the given name is present.</returns>
    public bool TryGetParameter(string name, out string? value)
    {
        if (parameters is not null)
        {
            foreach (HttpMediaTypeParameter parameter in parameters)
            {
                if (parameter.HasName(name))
                {
                    value = parameter.Value;
                    return true;
                }
            }
        }
        value = null;
        return false;
    }

    /// <summary>
    /// Determines whether this media range includes <paramref name="candidate"/>: the type and
    /// subtype match (respecting wildcards, case-insensitively) and every parameter constrained
    /// by this range is present on the candidate with an equal value. Extra parameters on the
    /// candidate are permitted (RFC 9110 &#167; 12.5.1).
    /// </summary>
    /// <param name="candidate">The concrete media type to test for membership.</param>
    /// <returns><see langword="true"/> when the candidate is acceptable to this range.</returns>
    public bool Includes(HttpMediaType candidate)
    {
        if (IsEmpty || candidate.IsEmpty)
        {
            return false;
        }
        if (!IsWildcardType && !string.Equals(Type, candidate.Type, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!IsWildcardSubType && !string.Equals(SubType, candidate.SubType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (parameters is not null)
        {
            foreach (HttpMediaTypeParameter parameter in parameters)
            {
                if (!candidate.TryGetParameter(parameter.Name, out string? candidateValue)
                    || !string.Equals(parameter.Value, candidateValue, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Parses <paramref name="value"/> as a media type or media range.
    /// </summary>
    /// <param name="value">The text to parse.</param>
    /// <returns>The parsed <see cref="HttpMediaType"/>.</returns>
    /// <exception cref="HttpException">The value is not a well-formed media type.</exception>
    public static HttpMediaType Parse(ReadOnlySpan<char> value)
    {
        if (!TryParse(value, out HttpMediaType result))
        {
            throw new HttpInvalidMediaTypeException($"The value is not a valid media type: '{value.ToString()}'.");
        }
        return result;
    }

    /// <summary>
    /// Attempts to parse <paramref name="value"/> as a media type or media range. Malformed
    /// parameters are skipped; a missing or malformed <c>type/subtype</c> fails the parse.
    /// </summary>
    /// <param name="value">The text to parse.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed media type.</param>
    /// <returns><see langword="true"/> when the value parses to a media type.</returns>
    public static bool TryParse(string? value, out HttpMediaType result)
        => TryParse(value.AsSpan(), out result);

    /// <summary>
    /// Attempts to parse <paramref name="value"/> as a media type or media range. Malformed
    /// parameters are skipped; a missing or malformed <c>type/subtype</c> fails the parse.
    /// </summary>
    /// <param name="value">The text to parse.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed media type.</param>
    /// <returns><see langword="true"/> when the value parses to a media type.</returns>
    public static bool TryParse(ReadOnlySpan<char> value, out HttpMediaType result)
    {
        result = default;

        ReadOnlySpan<char> trimmed = HttpFieldSyntax.TrimOws(value);
        if (trimmed.IsEmpty)
        {
            return false;
        }

        int semicolon = HttpFieldSyntax.IndexOfUnquoted(trimmed, ';');
        ReadOnlySpan<char> mediaPart = HttpFieldSyntax.TrimOws(semicolon < 0 ? trimmed : trimmed[..semicolon]);

        int slash = mediaPart.IndexOf('/');
        if (slash <= 0 || slash >= mediaPart.Length - 1)
        {
            return false;
        }

        ReadOnlySpan<char> typeSpan = HttpFieldSyntax.TrimOws(mediaPart[..slash]);
        ReadOnlySpan<char> subSpan = HttpFieldSyntax.TrimOws(mediaPart[(slash + 1)..]);
        if (!HttpFieldSyntax.IsToken(typeSpan) || !HttpFieldSyntax.IsToken(subSpan))
        {
            return false;
        }

        bool wildcardType = typeSpan.Length == 1 && typeSpan[0] == '*';
        bool wildcardSub = subSpan.Length == 1 && subSpan[0] == '*';

        // RFC 9110 §12.5.1: a wildcard type is only meaningful as the full "*/*" range.
        if (wildcardType && !wildcardSub)
        {
            return false;
        }

        string type = ToLowerString(typeSpan);
        string subType = ToLowerString(subSpan);

        HttpMediaTypeParameter[]? parsedParameters = null;
        if (semicolon >= 0)
        {
            parsedParameters = ParseParameters(trimmed[(semicolon + 1)..]);
        }

        result = new HttpMediaType(type, subType, parsedParameters);
        return true;
    }

    private static HttpMediaTypeParameter[]? ParseParameters(ReadOnlySpan<char> span)
    {
        List<HttpMediaTypeParameter>? list = null;

        while (!span.IsEmpty)
        {
            int next = HttpFieldSyntax.IndexOfUnquoted(span, ';');
            ReadOnlySpan<char> segment = HttpFieldSyntax.TrimOws(next < 0 ? span : span[..next]);
            span = next < 0 ? ReadOnlySpan<char>.Empty : span[(next + 1)..];

            if (segment.IsEmpty)
            {
                continue;
            }

            int equals = HttpFieldSyntax.IndexOfUnquoted(segment, '=');
            if (equals <= 0)
            {
                // A malformed parameter segment (no '=' or empty name) is tolerated and skipped.
                continue;
            }

            ReadOnlySpan<char> nameSpan = HttpFieldSyntax.TrimOws(segment[..equals]);
            ReadOnlySpan<char> valueSpan = HttpFieldSyntax.TrimOws(segment[(equals + 1)..]);
            if (!HttpFieldSyntax.IsToken(nameSpan))
            {
                continue;
            }

            string name = ToLowerString(nameSpan);
            string value = HttpFieldSyntax.UnquoteValue(valueSpan);
            (list ??= new List<HttpMediaTypeParameter>()).Add(new HttpMediaTypeParameter(name, value));
        }

        return list?.ToArray();
    }

    private static string ToLowerString(ReadOnlySpan<char> span)
    {
        Span<char> buffer = span.Length <= 128 ? stackalloc char[span.Length] : new char[span.Length];
        int written = span.ToLowerInvariant(buffer);
        return new string(buffer[..written]);
    }

    /// <inheritdoc />
    public bool Equals(HttpMediaType other)
    {
        if (!string.Equals(Type, other.Type, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(SubType, other.SubType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int count = parameters?.Length ?? 0;
        if (count != (other.parameters?.Length ?? 0))
        {
            return false;
        }
        if (count == 0)
        {
            return true;
        }

        foreach (HttpMediaTypeParameter parameter in parameters!)
        {
            if (!other.TryGetParameter(parameter.Name, out string? otherValue)
                || !string.Equals(parameter.Value, otherValue, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpMediaType other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        if (IsEmpty)
        {
            return 0;
        }
        return HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Type),
            StringComparer.OrdinalIgnoreCase.GetHashCode(SubType));
    }

    /// <inheritdoc />
    public override string ToString()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Type.Length + SubType.Length + 1);
        builder.Append(Type).Append('/').Append(SubType);
        if (parameters is not null)
        {
            foreach (HttpMediaTypeParameter parameter in parameters)
            {
                builder.Append("; ").Append(parameter.Name).Append('=');
                AppendValue(builder, parameter.Value);
            }
        }
        return builder.ToString();
    }

    private static void AppendValue(StringBuilder builder, string value)
    {
        if (value.Length > 0 && HttpFieldSyntax.IsToken(value.AsSpan()))
        {
            builder.Append(value);
            return;
        }

        builder.Append('"');
        foreach (char c in value)
        {
            if (c is '"' or '\\')
            {
                builder.Append('\\');
            }
            builder.Append(c);
        }
        builder.Append('"');
    }

    /// <summary>Determines whether two media types are semantically equal.</summary>
    public static bool operator ==(HttpMediaType left, HttpMediaType right) => left.Equals(right);

    /// <summary>Determines whether two media types are not semantically equal.</summary>
    public static bool operator !=(HttpMediaType left, HttpMediaType right) => !left.Equals(right);
}
