using System;
using System.Diagnostics;
using System.Globalization;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A single byte <c>range-spec</c> within a <c>Range</c> header (RFC 9110 &#167; 14.1.1). One of
/// three shapes: a closed <c>int-range</c> (<c>first-last</c>), an open-ended <c>int-range</c>
/// (<c>first-</c>, "from <c>first</c> to the end"), or a <c>suffix-range</c> (<c>-length</c>,
/// "the final <c>length</c> bytes").
/// </summary>
/// <remarks>
/// The positions are recorded exactly as the client stated them, independent of any content length.
/// <see cref="TryResolve(long, out long, out long)"/> applies RFC 9110 &#167; 14.1.2 to turn a spec
/// into a concrete <c>(offset, length)</c> slice against a known representation length, reporting
/// whether the spec is satisfiable.
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly struct HttpRange : IEquatable<HttpRange>
{
    private HttpRange(long? from, long? to, long? suffixLength)
    {
        From = from;
        To = to;
        SuffixLength = suffixLength;
    }

    /// <summary>
    /// Gets the first byte position of an <c>int-range</c>, or <see langword="null"/> when this is a
    /// <c>suffix-range</c>.
    /// </summary>
    public long? From { get; }

    /// <summary>
    /// Gets the last byte position (inclusive) of a closed <c>int-range</c>, or <see langword="null"/>
    /// for an open-ended <c>int-range</c> or a <c>suffix-range</c>.
    /// </summary>
    public long? To { get; }

    /// <summary>
    /// Gets the trailing byte count of a <c>suffix-range</c>, or <see langword="null"/> for an
    /// <c>int-range</c>.
    /// </summary>
    public long? SuffixLength { get; }

    /// <summary>Gets a value indicating whether this is a <c>suffix-range</c> (<c>-length</c>).</summary>
    public bool IsSuffix => SuffixLength.HasValue;

    /// <summary>Gets a value indicating whether this is an open-ended <c>int-range</c> (<c>first-</c>).</summary>
    public bool IsOpenEnded => From.HasValue && !To.HasValue;

    /// <summary>Gets a value indicating whether this instance was default-constructed (holds no range).</summary>
    public bool IsEmpty => !From.HasValue && !SuffixLength.HasValue;

    private string DebuggerDisplay => IsEmpty ? "<empty>" : ToString();

    /// <summary>
    /// Creates a closed <c>int-range</c> spanning <paramref name="from"/> to <paramref name="to"/> inclusive.
    /// </summary>
    /// <param name="from">The first byte position (zero-based).</param>
    /// <param name="to">The last byte position (inclusive).</param>
    /// <returns>The range spec.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="from"/> is negative or greater than <paramref name="to"/>.</exception>
    public static HttpRange FromTo(long from, long to)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfLessThan(to, from);
        return new HttpRange(from, to, null);
    }

    /// <summary>
    /// Creates an open-ended <c>int-range</c> from <paramref name="from"/> to the end of the representation.
    /// </summary>
    /// <param name="from">The first byte position (zero-based).</param>
    /// <returns>The range spec.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="from"/> is negative.</exception>
    public static HttpRange StartingAt(long from)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        return new HttpRange(from, null, null);
    }

    /// <summary>
    /// Creates a <c>suffix-range</c> selecting the final <paramref name="length"/> bytes of the representation.
    /// </summary>
    /// <param name="length">The number of trailing bytes to select.</param>
    /// <returns>The range spec.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    public static HttpRange Suffix(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new HttpRange(null, null, length);
    }

    /// <summary>
    /// Resolves this spec against a representation of <paramref name="completeLength"/> bytes,
    /// applying RFC 9110 &#167; 14.1.2 (open-ended and suffix ranges are clamped to the content;
    /// a <c>first-pos</c> at or beyond the end, or an empty suffix, is unsatisfiable).
    /// </summary>
    /// <param name="completeLength">The total length, in bytes, of the selected representation.</param>
    /// <param name="offset">When this method returns <see langword="true"/>, the zero-based offset of the first selected byte.</param>
    /// <param name="length">When this method returns <see langword="true"/>, the number of selected bytes (always &#8805; 1).</param>
    /// <returns><see langword="true"/> when the spec is satisfiable against <paramref name="completeLength"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="completeLength"/> is negative.</exception>
    public bool TryResolve(long completeLength, out long offset, out long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(completeLength);
        offset = 0;
        length = 0;

        if (IsEmpty || completeLength == 0)
        {
            return false;
        }

        if (IsSuffix)
        {
            long suffix = SuffixLength!.Value;
            if (suffix == 0)
            {
                // "-0" selects the final zero bytes: an empty, unsatisfiable range.
                return false;
            }
            long take = Math.Min(suffix, completeLength);
            offset = completeLength - take;
            length = take;
            return true;
        }

        long first = From!.Value;
        if (first >= completeLength)
        {
            // The range starts at or beyond the end of the representation → unsatisfiable.
            return false;
        }

        long last = To ?? completeLength - 1;
        if (last >= completeLength)
        {
            last = completeLength - 1;
        }

        offset = first;
        length = last - first + 1;
        return true;
    }

    /// <summary>
    /// Attempts to parse a single byte <c>range-spec</c> (RFC 9110 &#167; 14.1.1), for example
    /// <c>0-499</c>, <c>500-</c>, or <c>-500</c>.
    /// </summary>
    /// <param name="value">The range-spec text (surrounding whitespace is tolerated).</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed range spec.</param>
    /// <returns><see langword="true"/> when the value is a well-formed byte range-spec.</returns>
    public static bool TryParse(ReadOnlySpan<char> value, out HttpRange result)
    {
        result = default;

        ReadOnlySpan<char> spec = HttpFieldSyntax.TrimOws(value);
        if (spec.IsEmpty)
        {
            return false;
        }

        if (spec[0] == '-')
        {
            // suffix-range = "-" suffix-length
            if (!TryParseDigits(spec[1..], out long suffixLength))
            {
                return false;
            }
            result = new HttpRange(null, null, suffixLength);
            return true;
        }

        // int-range = first-pos "-" [ last-pos ]
        int dash = spec.IndexOf('-');
        if (dash <= 0)
        {
            return false;
        }

        if (!TryParseDigits(spec[..dash], out long first))
        {
            return false;
        }

        ReadOnlySpan<char> lastSpan = spec[(dash + 1)..];
        if (lastSpan.IsEmpty)
        {
            result = new HttpRange(first, null, null);
            return true;
        }

        if (!TryParseDigits(lastSpan, out long last) || last < first)
        {
            return false;
        }

        result = new HttpRange(first, last, null);
        return true;
    }

    private static bool TryParseDigits(ReadOnlySpan<char> value, out long result)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);

    /// <inheritdoc />
    public bool Equals(HttpRange other) => From == other.From && To == other.To && SuffixLength == other.SuffixLength;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpRange other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(From, To, SuffixLength);

    /// <summary>
    /// Renders the range-spec in wire form (<c>0-499</c>, <c>500-</c>, or <c>-500</c>) without the
    /// range unit.
    /// </summary>
    /// <returns>The range-spec text, or an empty string for a default-constructed instance.</returns>
    public override string ToString()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }
        if (IsSuffix)
        {
            return $"-{SuffixLength!.Value.ToString(CultureInfo.InvariantCulture)}";
        }
        string first = From!.Value.ToString(CultureInfo.InvariantCulture);
        return To.HasValue ? $"{first}-{To.Value.ToString(CultureInfo.InvariantCulture)}" : $"{first}-";
    }

    /// <summary>Determines whether two range specs are equal.</summary>
    public static bool operator ==(HttpRange left, HttpRange right) => left.Equals(right);

    /// <summary>Determines whether two range specs are not equal.</summary>
    public static bool operator !=(HttpRange left, HttpRange right) => !left.Equals(right);
}
