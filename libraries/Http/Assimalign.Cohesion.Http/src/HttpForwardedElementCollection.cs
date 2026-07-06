using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A parsed RFC 7239 <c>Forwarded</c> header — the comma-separated <c>1#forwarded-element</c> list.
/// Elements are held in wire order: the left-most element is the hop closest to the client and the
/// right-most is the hop closest to this server. A downstream trust evaluator walks the list
/// <em>right-to-left</em>, peeling off hops it trusts; <see cref="Reverse"/> and the index/count
/// surface support that traversal directly.
/// </summary>
/// <remarks>
/// <para>
/// Parsing is quote-aware (a comma inside a quoted value does not split the list) and strict: an
/// empty element between commas is ignored (RFC 7230 &#167; 7 list rule), but any element that is
/// present and malformed fails the whole parse (<see cref="TryParse(ReadOnlySpan{char}, out HttpForwardedElementCollection)"/>
/// returns <see langword="false"/>). This deterministic all-or-nothing behavior is deliberate: the
/// forwarded-headers middleware (issue #778) that applies the trust model must not silently drop a
/// malformed hop and mis-attribute the request to the wrong client. Parsing the protocol strictly
/// here keeps the security decision — which hops to trust — entirely in the middleware.
/// </para>
/// </remarks>
[DebuggerDisplay("Count = {Count}")]
public readonly struct HttpForwardedElementCollection : IReadOnlyList<HttpForwardedElement>, IEquatable<HttpForwardedElementCollection>
{
    private readonly HttpForwardedElement[]? elements;

    private HttpForwardedElementCollection(HttpForwardedElement[] elements)
    {
        this.elements = elements;
    }

    /// <summary>Gets an empty collection.</summary>
    public static HttpForwardedElementCollection Empty { get; } = new(Array.Empty<HttpForwardedElement>());

    /// <summary>Gets the number of elements in the list.</summary>
    public int Count => elements?.Length ?? 0;

    /// <summary>Gets the element at the given index, in wire order (left-most is index 0).</summary>
    /// <param name="index">The zero-based index.</param>
    /// <returns>The element at <paramref name="index"/>.</returns>
    public HttpForwardedElement this[int index]
        => elements is null ? throw new ArgumentOutOfRangeException(nameof(index)) : elements[index];

    /// <summary>Gets a value indicating whether the list has no elements.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>
    /// Gets the element added by the hop closest to this server (the right-most element), or
    /// <see langword="null"/> when the list is empty.
    /// </summary>
    public HttpForwardedElement? Nearest => Count == 0 ? null : elements![^1];

    /// <summary>Gets the elements as a span for allocation-free traversal in wire order.</summary>
    /// <returns>A span over the elements.</returns>
    public ReadOnlySpan<HttpForwardedElement> AsSpan() => elements;

    /// <summary>
    /// Returns a new collection with the elements in reverse (right-to-left / nearest-hop-first)
    /// order, the natural direction for trust evaluation.
    /// </summary>
    /// <returns>The reversed collection.</returns>
    public HttpForwardedElementCollection Reverse()
    {
        if (elements is null || elements.Length <= 1)
        {
            return this;
        }
        var reversed = new HttpForwardedElement[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            reversed[i] = elements[elements.Length - 1 - i];
        }
        return new HttpForwardedElementCollection(reversed);
    }

    /// <summary>
    /// Parses <paramref name="value"/> as a whole RFC 7239 <c>Forwarded</c> header value.
    /// </summary>
    /// <param name="value">The header value.</param>
    /// <returns>The parsed collection.</returns>
    /// <exception cref="HttpException">The value is not a well-formed, non-empty forwarded-element list.</exception>
    public static HttpForwardedElementCollection Parse(ReadOnlySpan<char> value)
    {
        if (!TryParse(value, out HttpForwardedElementCollection result))
        {
            throw new HttpInvalidForwardedException($"The value is not a valid Forwarded header: '{value.ToString()}'.");
        }
        return result;
    }

    /// <summary>
    /// Attempts to parse a multi-line <c>Forwarded</c> header. Repeated header lines are combined by
    /// comma (RFC 9110 &#167; 5.3), which is exactly the element separator, so multiple
    /// <c>Forwarded</c> field lines parse as one continuous list.
    /// </summary>
    /// <param name="value">The header value, possibly carrying multiple field lines.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed collection.</param>
    /// <returns><see langword="true"/> when the value is a well-formed, non-empty list.</returns>
    public static bool TryParse(HttpHeaderValue value, out HttpForwardedElementCollection result)
        => TryParse(value.Value.AsSpan(), out result);

    /// <summary>
    /// Attempts to parse <paramref name="value"/> as a whole <c>Forwarded</c> header value.
    /// </summary>
    /// <param name="value">The header value, or <see langword="null"/>.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed collection.</param>
    /// <returns><see langword="true"/> when the value is a well-formed, non-empty list.</returns>
    public static bool TryParse(string? value, out HttpForwardedElementCollection result)
        => TryParse(value.AsSpan(), out result);

    /// <summary>
    /// Attempts to parse <paramref name="value"/> as a whole <c>Forwarded</c> header value. Empty
    /// comma-separated slots are ignored; any present-but-malformed element fails the whole parse.
    /// Never throws.
    /// </summary>
    /// <param name="value">The header value.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed collection.</param>
    /// <returns><see langword="true"/> when the value is a well-formed, non-empty list.</returns>
    public static bool TryParse(ReadOnlySpan<char> value, out HttpForwardedElementCollection result)
    {
        result = default;

        List<HttpForwardedElement>? list = null;
        ReadOnlySpan<char> remaining = value;
        while (!remaining.IsEmpty)
        {
            int comma = HttpFieldSyntax.IndexOfUnquoted(remaining, ',');
            ReadOnlySpan<char> segment = HttpFieldSyntax.TrimOws(comma < 0 ? remaining : remaining[..comma]);
            remaining = comma < 0 ? ReadOnlySpan<char>.Empty : remaining[(comma + 1)..];

            if (segment.IsEmpty)
            {
                // Empty list elements are ignored per the RFC 7230 §7 list rule.
                continue;
            }

            if (!HttpForwardedElement.TryParse(segment, out HttpForwardedElement element))
            {
                return false;
            }

            (list ??= new List<HttpForwardedElement>()).Add(element);
        }

        if (list is null)
        {
            return false;
        }

        result = new HttpForwardedElementCollection(list.ToArray());
        return true;
    }

    /// <summary>
    /// Serializes the list to its RFC 7239 wire form (elements joined by <c>", "</c>).
    /// </summary>
    /// <returns>The wire form, or an empty string when the list is empty.</returns>
    public string Serialize()
    {
        if (elements is null || elements.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (int i = 0; i < elements.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }
            builder.Append(elements[i].Serialize());
        }
        return builder.ToString();
    }

    /// <inheritdoc cref="Serialize" />
    public override string ToString() => Serialize();

    /// <summary>Returns an enumerator over the elements in wire order.</summary>
    /// <returns>An enumerator.</returns>
    public IEnumerator<HttpForwardedElement> GetEnumerator()
        => ((IEnumerable<HttpForwardedElement>)(elements ?? Array.Empty<HttpForwardedElement>())).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public bool Equals(HttpForwardedElementCollection other)
    {
        int count = Count;
        if (count != other.Count)
        {
            return false;
        }
        for (int i = 0; i < count; i++)
        {
            if (!elements![i].Equals(other.elements![i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpForwardedElementCollection other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        if (elements is null)
        {
            return 0;
        }
        var hash = new HashCode();
        foreach (HttpForwardedElement element in elements)
        {
            hash.Add(element);
        }
        return hash.ToHashCode();
    }

    /// <summary>Determines whether two collections are equal.</summary>
    public static bool operator ==(HttpForwardedElementCollection left, HttpForwardedElementCollection right) => left.Equals(right);

    /// <summary>Determines whether two collections are not equal.</summary>
    public static bool operator !=(HttpForwardedElementCollection left, HttpForwardedElementCollection right) => !left.Equals(right);
}
