using System;
using System.Diagnostics;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A parsed entity-tag (RFC 9110 &#167; 8.8.3): the validator carried by the <c>ETag</c> response
/// field and by the <c>If-Match</c> / <c>If-None-Match</c> request fields. An entity-tag is an
/// opaque quoted string, optionally prefixed with the case-sensitive weakness indicator <c>W/</c>.
/// </summary>
/// <remarks>
/// <para>
/// The value stored in <see cref="Tag"/> is the opaque content <em>between</em> the quotes; the
/// surrounding <c>DQUOTE</c>s and the optional <c>W/</c> prefix are re-applied by
/// <see cref="ToString"/>. Two comparison functions are provided per RFC 9110 &#167; 8.8.3.2:
/// <see cref="StrongEquals(HttpEntityTag)"/> (both tags strong and octet-equal) and
/// <see cref="WeakEquals(HttpEntityTag)"/> (octet-equal regardless of weakness). <c>ETag</c>
/// caching validators use weak comparison; range and <c>If-Match</c> preconditions use strong
/// comparison.
/// </para>
/// <para>
/// This is a shared protocol primitive: caching (RFC 9111) and range/precondition evaluation
/// (RFC 9110 &#167; 13) both build on it rather than re-parsing the <c>ETag</c> grammar.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly struct HttpEntityTag : IEquatable<HttpEntityTag>
{
    private readonly string? tag;

    private HttpEntityTag(string tag, bool isWeak)
    {
        this.tag = tag;
        IsWeak = isWeak;
    }

    /// <summary>
    /// Gets the opaque tag content without the surrounding quotes (e.g. <c>xyzzy</c> for
    /// <c>"xyzzy"</c>). Empty when this instance was default-constructed.
    /// </summary>
    public string Tag => tag ?? string.Empty;

    /// <summary>
    /// Gets a value indicating whether this is a weak entity-tag (the <c>W/</c> prefix was present).
    /// </summary>
    public bool IsWeak { get; }

    /// <summary>
    /// Gets a value indicating whether this instance is uninitialized (default-constructed and never
    /// parsed).
    /// </summary>
    public bool IsEmpty => tag is null;

    private string DebuggerDisplay => IsEmpty ? "<empty>" : ToString();

    /// <summary>
    /// Creates a strong entity-tag with the supplied opaque content.
    /// </summary>
    /// <param name="tag">The opaque tag content, without surrounding quotes.</param>
    /// <returns>A strong <see cref="HttpEntityTag"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tag"/> is <see langword="null"/>.</exception>
    /// <exception cref="HttpException"><paramref name="tag"/> contains characters that are not valid opaque-tag characters.</exception>
    public static HttpEntityTag Strong(string tag) => Create(tag, isWeak: false);

    /// <summary>
    /// Creates a weak entity-tag (<c>W/</c>) with the supplied opaque content.
    /// </summary>
    /// <param name="tag">The opaque tag content, without surrounding quotes.</param>
    /// <returns>A weak <see cref="HttpEntityTag"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tag"/> is <see langword="null"/>.</exception>
    /// <exception cref="HttpException"><paramref name="tag"/> contains characters that are not valid opaque-tag characters.</exception>
    public static HttpEntityTag Weak(string tag) => Create(tag, isWeak: true);

    private static HttpEntityTag Create(string tag, bool isWeak)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (!IsValidOpaqueContent(tag.AsSpan()))
        {
            throw new HttpInvalidEntityTagException($"The value is not a valid entity-tag: '{tag}'.");
        }
        return new HttpEntityTag(tag, isWeak);
    }

    /// <summary>
    /// Parses <paramref name="value"/> as a single entity-tag.
    /// </summary>
    /// <param name="value">The entity-tag text (e.g. <c>"xyzzy"</c> or <c>W/"xyzzy"</c>).</param>
    /// <returns>The parsed <see cref="HttpEntityTag"/>.</returns>
    /// <exception cref="HttpException">The value is not a well-formed entity-tag.</exception>
    public static HttpEntityTag Parse(ReadOnlySpan<char> value)
    {
        if (!TryParse(value, out HttpEntityTag result))
        {
            throw new HttpInvalidEntityTagException($"The value is not a valid entity-tag: '{value.ToString()}'.");
        }
        return result;
    }

    /// <summary>
    /// Attempts to parse <paramref name="value"/> as a single entity-tag. The optional <c>W/</c>
    /// prefix is case-sensitive; the opaque content must be a valid RFC 9110 &#167; 8.8.3
    /// <c>etagc</c> sequence enclosed in <c>DQUOTE</c>s.
    /// </summary>
    /// <param name="value">The entity-tag text.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed entity-tag.</param>
    /// <returns><see langword="true"/> when the value is a well-formed entity-tag.</returns>
    public static bool TryParse(string? value, out HttpEntityTag result)
        => TryParse(value.AsSpan(), out result);

    /// <summary>
    /// Attempts to parse <paramref name="value"/> as a single entity-tag. The optional <c>W/</c>
    /// prefix is case-sensitive; the opaque content must be a valid RFC 9110 &#167; 8.8.3
    /// <c>etagc</c> sequence enclosed in <c>DQUOTE</c>s.
    /// </summary>
    /// <param name="value">The entity-tag text.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed entity-tag.</param>
    /// <returns><see langword="true"/> when the value is a well-formed entity-tag.</returns>
    public static bool TryParse(ReadOnlySpan<char> value, out HttpEntityTag result)
    {
        result = default;

        ReadOnlySpan<char> trimmed = HttpFieldSyntax.TrimOws(value);
        if (trimmed.Length < 2)
        {
            return false;
        }

        bool isWeak = false;
        // The weakness indicator "W/" is case-sensitive (RFC 9110 §8.8.3, %s"W/").
        if (trimmed[0] == 'W' && trimmed[1] == '/')
        {
            isWeak = true;
            trimmed = trimmed[2..];
        }

        if (trimmed.Length < 2 || trimmed[0] != '"' || trimmed[^1] != '"')
        {
            return false;
        }

        ReadOnlySpan<char> content = trimmed[1..^1];
        if (!IsValidOpaqueContent(content))
        {
            return false;
        }

        result = new HttpEntityTag(content.ToString(), isWeak);
        return true;
    }

    private static bool IsValidOpaqueContent(ReadOnlySpan<char> content)
    {
        // etagc = %x21 / %x23-7E / obs-text  (RFC 9110 §8.8.3): every VCHAR except DQUOTE,
        // plus obs-text (%x80-FF). Excludes SP, CTL, DEL, and the quote character itself.
        foreach (char c in content)
        {
            bool valid = c == '\x21' || (c >= '\x23' && c <= '\x7E') || (c >= '\x80' && c <= '\xFF');
            if (!valid)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Compares this entity-tag with <paramref name="other"/> using the RFC 9110 &#167; 8.8.3.2
    /// <em>strong</em> comparison function: the tags are equivalent only when neither is weak and
    /// their opaque content is octet-for-octet identical.
    /// </summary>
    /// <param name="other">The entity-tag to compare against.</param>
    /// <returns><see langword="true"/> when the tags are strongly equivalent.</returns>
    public bool StrongEquals(HttpEntityTag other)
        => !IsWeak && !other.IsWeak && OpaqueEquals(other);

    /// <summary>
    /// Compares this entity-tag with <paramref name="other"/> using the RFC 9110 &#167; 8.8.3.2
    /// <em>weak</em> comparison function: the tags are equivalent when their opaque content is
    /// octet-for-octet identical, regardless of whether either is weak.
    /// </summary>
    /// <param name="other">The entity-tag to compare against.</param>
    /// <returns><see langword="true"/> when the tags are weakly equivalent.</returns>
    public bool WeakEquals(HttpEntityTag other)
        => !IsEmpty && !other.IsEmpty && OpaqueEquals(other);

    private bool OpaqueEquals(HttpEntityTag other)
        => string.Equals(Tag, other.Tag, StringComparison.Ordinal);

    /// <summary>
    /// Determines structural equality: the opaque content and the weakness flag are both equal.
    /// This is stricter than <see cref="StrongEquals(HttpEntityTag)"/>; use the comparison methods
    /// for HTTP validator semantics.
    /// </summary>
    /// <param name="other">The entity-tag to compare against.</param>
    /// <returns><see langword="true"/> when both tags are structurally identical.</returns>
    public bool Equals(HttpEntityTag other)
        => IsWeak == other.IsWeak && string.Equals(tag, other.tag, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpEntityTag other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(IsWeak, tag is null ? 0 : string.GetHashCode(tag, StringComparison.Ordinal));

    /// <summary>
    /// Renders the entity-tag in wire form (e.g. <c>"xyzzy"</c> or <c>W/"xyzzy"</c>).
    /// </summary>
    /// <returns>The entity-tag as it appears in a field value.</returns>
    public override string ToString()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }
        return IsWeak ? $"W/\"{Tag}\"" : $"\"{Tag}\"";
    }

    /// <summary>Determines structural equality between two entity-tags.</summary>
    public static bool operator ==(HttpEntityTag left, HttpEntityTag right) => left.Equals(right);

    /// <summary>Determines structural inequality between two entity-tags.</summary>
    public static bool operator !=(HttpEntityTag left, HttpEntityTag right) => !left.Equals(right);
}
