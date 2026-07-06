using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A parsed <c>Range</c> request header (RFC 9110 &#167; 14.2): a range unit followed by a
/// non-empty, ordered set of <see cref="HttpRange"/> specs. Only the <c>bytes</c> unit is
/// interpreted — a header naming any other (or an unrecognized) unit fails to parse, which is the
/// signal for a server to ignore the range and serve the full representation (RFC 9110 &#167; 14.2).
/// </summary>
/// <remarks>
/// Parsing is deliberately strict: a syntactically invalid range-set fails as a whole rather than
/// silently dropping the malformed members, so a caller that gets a parsed header can trust every
/// spec in it. Whether the set produces a <c>206</c> or a <c>416</c> is a separate decision made by
/// <see cref="HttpRangeSelector"/> against a known content length.
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly struct HttpRangeHeader
{
    /// <summary>The <c>bytes</c> range unit — the only unit defined for HTTP range requests.</summary>
    public const string BytesUnit = "bytes";

    private readonly HttpRange[]? ranges;

    private HttpRangeHeader(string unit, HttpRange[] ranges)
    {
        Unit = unit;
        this.ranges = ranges;
    }

    /// <summary>Gets the range unit; <see cref="BytesUnit"/> for any successfully parsed header.</summary>
    public string Unit { get; }

    /// <summary>Gets the ordered set of range specs. Never empty for a parsed header.</summary>
    public IReadOnlyList<HttpRange> Ranges
        => ranges ?? (IReadOnlyList<HttpRange>)Array.Empty<HttpRange>();

    /// <summary>Gets the number of range specs.</summary>
    public int Count => ranges?.Length ?? 0;

    /// <summary>Gets a value indicating whether this instance was default-constructed (holds no ranges).</summary>
    public bool IsEmpty => ranges is null;

    private string DebuggerDisplay => IsEmpty ? "<empty>" : ToString();

    /// <summary>
    /// Parses a <c>Range</c> header value (RFC 9110 &#167; 14.1.1), for example <c>bytes=0-499,-500</c>.
    /// </summary>
    /// <param name="value">The header value text.</param>
    /// <returns>The parsed <see cref="HttpRangeHeader"/>.</returns>
    /// <exception cref="HttpException">The value is not a well-formed byte-range header.</exception>
    public static HttpRangeHeader Parse(ReadOnlySpan<char> value)
    {
        if (!TryParse(value, out HttpRangeHeader result))
        {
            throw new HttpInvalidRangeException($"The value is not a valid byte-range header: '{value.ToString()}'.");
        }
        return result;
    }

    /// <summary>
    /// Attempts to parse a <c>Range</c> header value (RFC 9110 &#167; 14.1.1).
    /// </summary>
    /// <param name="value">The header value text.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed header.</param>
    /// <returns><see langword="true"/> when the value is a well-formed byte-range header.</returns>
    public static bool TryParse(string? value, out HttpRangeHeader result)
        => TryParse(value.AsSpan(), out result);

    /// <summary>
    /// Attempts to parse a <c>Range</c> header value (RFC 9110 &#167; 14.1.1). Returns
    /// <see langword="false"/> for an unrecognized range unit or any syntactically invalid range-set —
    /// both cases the caller should treat as "no usable range" and serve the full representation.
    /// </summary>
    /// <param name="value">The header value text.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed header.</param>
    /// <returns><see langword="true"/> when the value is a well-formed byte-range header.</returns>
    public static bool TryParse(ReadOnlySpan<char> value, out HttpRangeHeader result)
    {
        result = default;

        ReadOnlySpan<char> trimmed = HttpFieldSyntax.TrimOws(value);
        int equals = trimmed.IndexOf('=');
        if (equals <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> unitSpan = HttpFieldSyntax.TrimOws(trimmed[..equals]);
        // Range units are case-insensitive tokens (RFC 9110 § 14.1); only "bytes" is interpreted here.
        if (!unitSpan.Equals(BytesUnit, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> setSpan = trimmed[(equals + 1)..];
        List<HttpRange>? list = null;
        while (!setSpan.IsEmpty)
        {
            int comma = setSpan.IndexOf(',');
            ReadOnlySpan<char> segment = comma < 0 ? setSpan : setSpan[..comma];
            setSpan = comma < 0 ? ReadOnlySpan<char>.Empty : setSpan[(comma + 1)..];

            ReadOnlySpan<char> spec = HttpFieldSyntax.TrimOws(segment);
            if (spec.IsEmpty)
            {
                // The list rule (1#) permits and skips empty elements.
                continue;
            }

            if (!HttpRange.TryParse(spec, out HttpRange range))
            {
                // Any malformed member invalidates the whole set (strict parse → caller ignores range).
                return false;
            }
            (list ??= new List<HttpRange>()).Add(range);
        }

        if (list is null || list.Count == 0)
        {
            return false;
        }

        result = new HttpRangeHeader(BytesUnit, list.ToArray());
        return true;
    }

    /// <summary>
    /// Renders the header in wire form (e.g. <c>bytes=0-499,-500</c>).
    /// </summary>
    /// <returns>The header value text, or an empty string for a default-constructed instance.</returns>
    public override string ToString()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Unit.Length + 1 + ranges!.Length * 8);
        builder.Append(Unit).Append('=');
        for (int i = 0; i < ranges.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }
            builder.Append(ranges[i].ToString());
        }
        return builder.ToString();
    }
}
