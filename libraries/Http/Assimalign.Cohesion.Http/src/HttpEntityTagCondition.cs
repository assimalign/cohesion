using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A parsed <c>If-Match</c> or <c>If-None-Match</c> field value (RFC 9110 &#167; 13.1.1 /
/// &#167; 13.1.2): either the <c>*</c> wildcard or a comma-separated list of entity-tags.
/// </summary>
/// <remarks>
/// <para>
/// The two matching methods encode the two comparison functions the preconditions require:
/// <see cref="MatchesStrong(HttpEntityTag?, bool)"/> (strong comparison, used by <c>If-Match</c>
/// and by <c>If-Range</c>'s entity-tag form) and <see cref="MatchesWeak(HttpEntityTag?, bool)"/>
/// (weak comparison, used by <c>If-None-Match</c>). For the <c>*</c> wildcard both reduce to
/// &#8220;does the target resource currently have a representation&#8221;.
/// </para>
/// <para>
/// This is a shared protocol primitive: caching (RFC 9111) and range/precondition evaluation
/// (RFC 9110 &#167; 13) both consume it rather than re-parsing the list grammar.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly struct HttpEntityTagCondition
{
    private readonly HttpEntityTag[]? tags;

    private HttpEntityTagCondition(bool isAny, HttpEntityTag[]? tags)
    {
        IsAny = isAny;
        this.tags = tags;
    }

    /// <summary>
    /// Gets a value indicating whether the condition is the <c>*</c> wildcard, which matches any
    /// current representation of the target resource.
    /// </summary>
    public bool IsAny { get; }

    /// <summary>
    /// Gets the listed entity-tags. Empty when the condition is the <c>*</c> wildcard.
    /// </summary>
    public IReadOnlyList<HttpEntityTag> Tags => tags ?? (IReadOnlyList<HttpEntityTag>)Array.Empty<HttpEntityTag>();

    private string DebuggerDisplay => IsAny ? "*" : ToString();

    /// <summary>
    /// Parses <paramref name="value"/> as an <c>If-Match</c> / <c>If-None-Match</c> field value.
    /// </summary>
    /// <param name="value">The field text (e.g. <c>*</c> or <c>"a", W/"b"</c>).</param>
    /// <returns>The parsed <see cref="HttpEntityTagCondition"/>.</returns>
    /// <exception cref="HttpException">The value is not a well-formed condition.</exception>
    public static HttpEntityTagCondition Parse(ReadOnlySpan<char> value)
    {
        if (!TryParse(value, out HttpEntityTagCondition result))
        {
            throw new HttpInvalidEntityTagException($"The value is not a valid If-Match/If-None-Match condition: '{value.ToString()}'.");
        }
        return result;
    }

    /// <summary>
    /// Attempts to parse <paramref name="value"/> as an <c>If-Match</c> / <c>If-None-Match</c>
    /// field value. The <c>*</c> wildcard must appear alone; a list member that is not a
    /// well-formed entity-tag fails the whole parse. Empty list elements are ignored per
    /// RFC 9110 &#167; 5.6.1.2.
    /// </summary>
    /// <param name="value">The field text.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed condition.</param>
    /// <returns><see langword="true"/> when the value is a well-formed condition.</returns>
    public static bool TryParse(string? value, out HttpEntityTagCondition result)
        => TryParse(value.AsSpan(), out result);

    /// <summary>
    /// Attempts to parse <paramref name="value"/> as an <c>If-Match</c> / <c>If-None-Match</c>
    /// field value. The <c>*</c> wildcard must appear alone; a list member that is not a
    /// well-formed entity-tag fails the whole parse. Empty list elements are ignored per
    /// RFC 9110 &#167; 5.6.1.2.
    /// </summary>
    /// <param name="value">The field text.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed condition.</param>
    /// <returns><see langword="true"/> when the value is a well-formed condition.</returns>
    public static bool TryParse(ReadOnlySpan<char> value, out HttpEntityTagCondition result)
    {
        result = default;

        ReadOnlySpan<char> trimmed = HttpFieldSyntax.TrimOws(value);
        if (trimmed.IsEmpty)
        {
            return false;
        }

        if (trimmed.Length == 1 && trimmed[0] == '*')
        {
            result = new HttpEntityTagCondition(isAny: true, tags: null);
            return true;
        }

        List<HttpEntityTag>? parsed = null;
        while (!trimmed.IsEmpty)
        {
            int comma = HttpFieldSyntax.IndexOfUnquoted(trimmed, ',');
            ReadOnlySpan<char> segment = HttpFieldSyntax.TrimOws(comma < 0 ? trimmed : trimmed[..comma]);
            trimmed = comma < 0 ? ReadOnlySpan<char>.Empty : trimmed[(comma + 1)..];

            if (segment.IsEmpty)
            {
                // Ignore empty list elements (RFC 9110 §5.6.1.2).
                continue;
            }

            if (!HttpEntityTag.TryParse(segment, out HttpEntityTag tag))
            {
                return false;
            }

            (parsed ??= new List<HttpEntityTag>()).Add(tag);
        }

        if (parsed is null)
        {
            // Only empty elements were present (e.g. ",") — not a valid condition.
            return false;
        }

        result = new HttpEntityTagCondition(isAny: false, tags: parsed.ToArray());
        return true;
    }

    /// <summary>
    /// Evaluates the condition using the RFC 9110 &#167; 8.8.3.2 <em>strong</em> comparison function
    /// (the form <c>If-Match</c> and <c>If-Range</c> use): <c>*</c> matches when a current
    /// representation exists; otherwise the condition matches when any listed tag strongly equals
    /// <paramref name="current"/>.
    /// </summary>
    /// <param name="current">The target resource's current entity-tag, or <see langword="null"/> when it has none.</param>
    /// <param name="hasCurrentRepresentation"><see langword="true"/> when the target resource currently has a representation.</param>
    /// <returns><see langword="true"/> when the condition matches under strong comparison.</returns>
    public bool MatchesStrong(HttpEntityTag? current, bool hasCurrentRepresentation)
    {
        if (IsAny)
        {
            return hasCurrentRepresentation;
        }
        if (current is not { } tag || tags is null)
        {
            return false;
        }
        foreach (HttpEntityTag candidate in tags)
        {
            if (candidate.StrongEquals(tag))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Evaluates the condition using the RFC 9110 &#167; 8.8.3.2 <em>weak</em> comparison function
    /// (the form <c>If-None-Match</c> uses): <c>*</c> matches when a current representation exists;
    /// otherwise the condition matches when any listed tag weakly equals <paramref name="current"/>.
    /// </summary>
    /// <param name="current">The target resource's current entity-tag, or <see langword="null"/> when it has none.</param>
    /// <param name="hasCurrentRepresentation"><see langword="true"/> when the target resource currently has a representation.</param>
    /// <returns><see langword="true"/> when the condition matches under weak comparison.</returns>
    public bool MatchesWeak(HttpEntityTag? current, bool hasCurrentRepresentation)
    {
        if (IsAny)
        {
            return hasCurrentRepresentation;
        }
        if (current is not { } tag || tags is null)
        {
            return false;
        }
        foreach (HttpEntityTag candidate in tags)
        {
            if (candidate.WeakEquals(tag))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Renders the condition in wire form (<c>*</c> or a comma-separated entity-tag list).
    /// </summary>
    /// <returns>The condition as it appears in a field value.</returns>
    public override string ToString()
    {
        if (IsAny)
        {
            return "*";
        }
        if (tags is null || tags.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (int i = 0; i < tags.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }
            builder.Append(tags[i].ToString());
        }
        return builder.ToString();
    }
}
