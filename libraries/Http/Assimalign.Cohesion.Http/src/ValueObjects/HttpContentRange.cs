using System;
using System.Diagnostics;
using System.Globalization;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A parsed <c>Content-Range</c> response header (RFC 9110 &#167; 14.4). It appears in one of three
/// shapes: a satisfied range with a known complete length (<c>bytes 0-499/1234</c>), a satisfied
/// range whose complete length is unknown (<c>bytes 0-499/*</c>), or an unsatisfied range that
/// reports only the complete length (<c>bytes */1234</c>) — the form a <c>416 Range Not
/// Satisfiable</c> response carries.
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly struct HttpContentRange : IEquatable<HttpContentRange>
{
    private HttpContentRange(string unit, long? from, long? to, long? length)
    {
        Unit = unit;
        From = from;
        To = to;
        Length = length;
    }

    /// <summary>Gets the range unit; <see cref="HttpRangeHeader.BytesUnit"/> for any parsed instance.</summary>
    public string Unit { get; }

    /// <summary>Gets the first byte position of the represented range, or <see langword="null"/> for the unsatisfied (<c>*/N</c>) form.</summary>
    public long? From { get; }

    /// <summary>Gets the last byte position (inclusive) of the represented range, or <see langword="null"/> for the unsatisfied form.</summary>
    public long? To { get; }

    /// <summary>Gets the total length of the representation, or <see langword="null"/> when it is unknown (the <c>/*</c> form).</summary>
    public long? Length { get; }

    /// <summary>Gets a value indicating whether this is the unsatisfied (<c>bytes */N</c>) form.</summary>
    public bool IsUnsatisfied => !From.HasValue;

    /// <summary>Gets a value indicating whether the complete representation length is known.</summary>
    public bool HasCompleteLength => Length.HasValue;

    /// <summary>Gets a value indicating whether this instance was default-constructed.</summary>
    public bool IsEmpty => Unit is null;

    private string DebuggerDisplay => IsEmpty ? "<empty>" : ToString();

    /// <summary>
    /// Creates a satisfied <c>Content-Range</c> for the byte slice <paramref name="from"/>..<paramref name="to"/>.
    /// </summary>
    /// <param name="from">The first byte position (zero-based).</param>
    /// <param name="to">The last byte position (inclusive).</param>
    /// <param name="completeLength">The total representation length, or <see langword="null"/> when unknown (emits <c>/*</c>).</param>
    /// <returns>The satisfied content-range.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="from"/> is negative, <paramref name="to"/> is less than <paramref name="from"/>, or <paramref name="completeLength"/> is not greater than <paramref name="to"/>.</exception>
    public static HttpContentRange Satisfied(long from, long to, long? completeLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfLessThan(to, from);
        if (completeLength is long length)
        {
            // RFC 9110 § 14.4: last-pos must be strictly less than complete-length.
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(length, to);
        }
        return new HttpContentRange(HttpRangeHeader.BytesUnit, from, to, completeLength);
    }

    /// <summary>
    /// Creates an unsatisfied <c>Content-Range</c> (<c>bytes */N</c>) reporting the complete length —
    /// the form that accompanies a <c>416</c> response.
    /// </summary>
    /// <param name="completeLength">The total representation length.</param>
    /// <returns>The unsatisfied content-range.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="completeLength"/> is negative.</exception>
    public static HttpContentRange Unsatisfied(long completeLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(completeLength);
        return new HttpContentRange(HttpRangeHeader.BytesUnit, null, null, completeLength);
    }

    /// <summary>
    /// Parses a <c>Content-Range</c> header value (RFC 9110 &#167; 14.4).
    /// </summary>
    /// <param name="value">The header value text.</param>
    /// <returns>The parsed <see cref="HttpContentRange"/>.</returns>
    /// <exception cref="HttpException">The value is not a well-formed content-range.</exception>
    public static HttpContentRange Parse(ReadOnlySpan<char> value)
    {
        if (!TryParse(value, out HttpContentRange result))
        {
            throw new HttpInvalidContentRangeException($"The value is not a valid content-range: '{value.ToString()}'.");
        }
        return result;
    }

    /// <summary>
    /// Attempts to parse a <c>Content-Range</c> header value (RFC 9110 &#167; 14.4).
    /// </summary>
    /// <param name="value">The header value text.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed content-range.</param>
    /// <returns><see langword="true"/> when the value is a well-formed byte content-range.</returns>
    public static bool TryParse(string? value, out HttpContentRange result)
        => TryParse(value.AsSpan(), out result);

    /// <summary>
    /// Attempts to parse a <c>Content-Range</c> header value (RFC 9110 &#167; 14.4). Only the
    /// <c>bytes</c> unit is interpreted.
    /// </summary>
    /// <param name="value">The header value text.</param>
    /// <param name="result">When this method returns <see langword="true"/>, the parsed content-range.</param>
    /// <returns><see langword="true"/> when the value is a well-formed byte content-range.</returns>
    public static bool TryParse(ReadOnlySpan<char> value, out HttpContentRange result)
    {
        result = default;

        ReadOnlySpan<char> trimmed = HttpFieldSyntax.TrimOws(value);
        int space = trimmed.IndexOf(' ');
        if (space <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> unitSpan = trimmed[..space];
        if (!unitSpan.Equals(HttpRangeHeader.BytesUnit, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> body = HttpFieldSyntax.TrimOws(trimmed[(space + 1)..]);
        int slash = body.IndexOf('/');
        if (slash <= 0 || slash >= body.Length - 1)
        {
            return false;
        }

        ReadOnlySpan<char> rangePart = body[..slash];
        ReadOnlySpan<char> lengthPart = body[(slash + 1)..];

        long? completeLength;
        if (lengthPart.Length == 1 && lengthPart[0] == '*')
        {
            completeLength = null;
        }
        else if (TryParseDigits(lengthPart, out long parsedLength))
        {
            completeLength = parsedLength;
        }
        else
        {
            return false;
        }

        // unsatisfied-range = "*" "/" complete-length
        if (rangePart.Length == 1 && rangePart[0] == '*')
        {
            if (completeLength is null)
            {
                // "*/*" is not a valid Content-Range.
                return false;
            }
            result = new HttpContentRange(HttpRangeHeader.BytesUnit, null, null, completeLength);
            return true;
        }

        // incl-range = first-pos "-" last-pos
        int dash = rangePart.IndexOf('-');
        if (dash <= 0)
        {
            return false;
        }
        if (!TryParseDigits(rangePart[..dash], out long from) || !TryParseDigits(rangePart[(dash + 1)..], out long to))
        {
            return false;
        }
        if (to < from || (completeLength is long len && to >= len))
        {
            return false;
        }

        result = new HttpContentRange(HttpRangeHeader.BytesUnit, from, to, completeLength);
        return true;
    }

    private static bool TryParseDigits(ReadOnlySpan<char> value, out long result)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);

    /// <inheritdoc />
    public bool Equals(HttpContentRange other)
        => From == other.From && To == other.To && Length == other.Length
        && string.Equals(Unit, other.Unit, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is HttpContentRange other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(From, To, Length, Unit is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Unit));

    /// <summary>
    /// Renders the header in wire form (<c>bytes 0-499/1234</c>, <c>bytes 0-499/*</c>, or <c>bytes */1234</c>).
    /// </summary>
    /// <returns>The header value text, or an empty string for a default-constructed instance.</returns>
    public override string ToString()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        string length = Length.HasValue ? Length.Value.ToString(CultureInfo.InvariantCulture) : "*";
        if (IsUnsatisfied)
        {
            return $"{Unit} */{length}";
        }
        string from = From!.Value.ToString(CultureInfo.InvariantCulture);
        string to = To!.Value.ToString(CultureInfo.InvariantCulture);
        return $"{Unit} {from}-{to}/{length}";
    }

    /// <summary>Determines whether two content-ranges are equal.</summary>
    public static bool operator ==(HttpContentRange left, HttpContentRange right) => left.Equals(right);

    /// <summary>Determines whether two content-ranges are not equal.</summary>
    public static bool operator !=(HttpContentRange left, HttpContentRange right) => !left.Equals(right);
}
