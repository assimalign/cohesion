using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The ordered list of entries carried by one of the de-facto <c>X-Forwarded-For</c>,
/// <c>X-Forwarded-Proto</c>, or <c>X-Forwarded-Host</c> headers. Each proxy <em>appends</em> the
/// value it observed, so entries are in wire order — the left-most is the original client (or the
/// value seen at the first hop) and the right-most is what the nearest proxy observed. A trust
/// evaluator walks the list right-to-left; <see cref="Reverse"/>, <see cref="Nearest"/>, and the
/// index/count surface support that traversal.
/// </summary>
/// <remarks>
/// <para>
/// This value object owns only the <em>list structure</em>: it splits a header value on commas
/// (quote-aware), trims optional whitespace, and joins the multiple field lines that arise when a
/// header occurs more than once (RFC 9110 &#167; 5.3 combines repeated lines by comma, which is the
/// same separator). Entries are preserved verbatim; interpreting an <c>X-Forwarded-For</c> entry as
/// an address is a consumer concern — pass it to <see cref="HttpForwardedNode.TryParse(ReadOnlySpan{char}, out HttpForwardedNode)"/>,
/// which accepts the bracketed and bare IPv6 spellings that appear in the wild. Keeping the address
/// and trust semantics out of this type lets the same primitive serve all three headers, whose
/// entry meanings differ (address vs. scheme vs. host).
/// </para>
/// <para>
/// Parsing never throws and is deterministic on hostile input: empty comma slots are dropped, and a
/// value that yields no entries returns <see langword="false"/> from
/// <see cref="TryParse(ReadOnlySpan{char}, out HttpForwardedValues)"/>.
/// </para>
/// </remarks>
[DebuggerDisplay("Count = {Count}")]
public readonly struct HttpForwardedValues : IReadOnlyList<string>, IEquatable<HttpForwardedValues>
{
    private readonly string[]? entries;

    private HttpForwardedValues(string[] entries)
    {
        this.entries = entries;
    }

    /// <summary>Gets an empty list.</summary>
    public static HttpForwardedValues Empty { get; } = new(Array.Empty<string>());

    /// <summary>Gets the number of entries.</summary>
    public int Count => entries?.Length ?? 0;

    /// <summary>Gets the entry at the given index, in wire order (left-most is index 0).</summary>
    /// <param name="index">The zero-based index.</param>
    /// <returns>The entry at <paramref name="index"/>.</returns>
    public string this[int index]
        => entries is null ? throw new ArgumentOutOfRangeException(nameof(index)) : entries[index];

    /// <summary>Gets a value indicating whether the list has no entries.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>
    /// Gets the entry recorded by the hop closest to this server (the right-most entry), or
    /// <see langword="null"/> when the list is empty.
    /// </summary>
    public string? Nearest => Count == 0 ? null : entries![^1];

    /// <summary>Gets the entries as a span for allocation-free traversal in wire order.</summary>
    /// <returns>A span over the entries.</returns>
    public ReadOnlySpan<string> AsSpan() => entries;

    /// <summary>
    /// Returns a new list with the entries in reverse (right-to-left / nearest-hop-first) order, the
    /// natural direction for trust evaluation.
    /// </summary>
    /// <returns>The reversed list.</returns>
    public HttpForwardedValues Reverse()
    {
        if (entries is null || entries.Length <= 1)
        {
            return this;
        }
        var reversed = new string[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            reversed[i] = entries[entries.Length - 1 - i];
        }
        return new HttpForwardedValues(reversed);
    }

    /// <summary>
    /// Parses <paramref name="value"/> as an <c>X-Forwarded-*</c> list value.
    /// </summary>
    /// <param name="value">The header value.</param>
    /// <returns>The parsed list.</returns>
    /// <exception cref="HttpException">The value contains no entries.</exception>
    public static HttpForwardedValues Parse(ReadOnlySpan<char> value)
    {
        if (!TryParse(value, out HttpForwardedValues result))
        {
            throw new HttpInvalidForwardedException($"The value is not a valid X-Forwarded-* list: '{value.ToString()}'.");
        }
        return result;
    }

    /// <summary>
    /// Attempts to parse a possibly multi-line <c>X-Forwarded-*</c> header. Repeated header lines are
    /// combined by comma, so multiple occurrences parse as one continuous list in arrival order.
    /// </summary>
    /// <param name="value">The header value, possibly carrying multiple field lines.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed list.</param>
    /// <returns><see langword="true"/> when at least one entry was parsed.</returns>
    public static bool TryParse(HttpHeaderValue value, out HttpForwardedValues result)
        => TryParse(value.Value.AsSpan(), out result);

    /// <summary>
    /// Attempts to parse <paramref name="value"/> as an <c>X-Forwarded-*</c> list value.
    /// </summary>
    /// <param name="value">The header value, or <see langword="null"/>.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed list.</param>
    /// <returns><see langword="true"/> when at least one entry was parsed.</returns>
    public static bool TryParse(string? value, out HttpForwardedValues result)
        => TryParse(value.AsSpan(), out result);

    /// <summary>
    /// Attempts to parse <paramref name="value"/> as an <c>X-Forwarded-*</c> list value. Empty
    /// comma slots are dropped; a value with no entries yields <see langword="false"/>. Never throws.
    /// </summary>
    /// <param name="value">The header value.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed list.</param>
    /// <returns><see langword="true"/> when at least one entry was parsed.</returns>
    public static bool TryParse(ReadOnlySpan<char> value, out HttpForwardedValues result)
    {
        result = default;

        List<string>? list = null;
        ReadOnlySpan<char> remaining = value;
        while (!remaining.IsEmpty)
        {
            int comma = HttpFieldSyntax.IndexOfUnquoted(remaining, ',');
            ReadOnlySpan<char> segment = HttpFieldSyntax.TrimOws(comma < 0 ? remaining : remaining[..comma]);
            remaining = comma < 0 ? ReadOnlySpan<char>.Empty : remaining[(comma + 1)..];

            if (segment.IsEmpty)
            {
                continue;
            }

            (list ??= new List<string>()).Add(segment.ToString());
        }

        if (list is null)
        {
            return false;
        }

        result = new HttpForwardedValues(list.ToArray());
        return true;
    }

    /// <summary>
    /// Serializes the list to wire form (entries joined by <c>", "</c>).
    /// </summary>
    /// <returns>The wire form, or an empty string when the list is empty.</returns>
    public string Serialize()
        => entries is null || entries.Length == 0 ? string.Empty : string.Join(", ", entries);

    /// <inheritdoc cref="Serialize" />
    public override string ToString() => Serialize();

    /// <summary>Returns an enumerator over the entries in wire order.</summary>
    /// <returns>An enumerator.</returns>
    public IEnumerator<string> GetEnumerator()
        => ((IEnumerable<string>)(entries ?? Array.Empty<string>())).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public bool Equals(HttpForwardedValues other)
    {
        int count = Count;
        if (count != other.Count)
        {
            return false;
        }
        for (int i = 0; i < count; i++)
        {
            if (!string.Equals(entries![i], other.entries![i], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpForwardedValues other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        if (entries is null)
        {
            return 0;
        }
        var hash = new HashCode();
        foreach (string entry in entries)
        {
            hash.Add(entry, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    /// <summary>Determines whether two lists are equal.</summary>
    public static bool operator ==(HttpForwardedValues left, HttpForwardedValues right) => left.Equals(right);

    /// <summary>Determines whether two lists are not equal.</summary>
    public static bool operator !=(HttpForwardedValues left, HttpForwardedValues right) => !left.Equals(right);
}
