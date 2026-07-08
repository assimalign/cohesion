using System;
using System.Globalization;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Parses and formats HTTP-date values (RFC 9110 &#167; 5.6.7) — the timestamp form carried by
/// fields such as <c>Date</c>, <c>Last-Modified</c>, <c>Expires</c>, <c>If-Modified-Since</c>, and
/// <c>If-Unmodified-Since</c>.
/// </summary>
/// <remarks>
/// <para>
/// A sender MUST emit the preferred <em>IMF-fixdate</em> form
/// (<c>Sun, 06 Nov 1994 08:49:37 GMT</c>); a recipient MUST also accept the two obsolete forms —
/// the RFC 850 form (<c>Sunday, 06-Nov-94 08:49:37 GMT</c>) and the asctime form
/// (<c>Sun Nov  6 08:49:37 1994</c>). <see cref="TryParse(ReadOnlySpan{char}, out DateTimeOffset)"/>
/// accepts all three; <see cref="Format(DateTimeOffset)"/> always emits IMF-fixdate.
/// </para>
/// <para>
/// All timestamps are in GMT (UTC); parsed values are returned as a <see cref="DateTimeOffset"/>
/// with a zero offset. Following RFC 9110 &#167; 13.1.3, a caller treats a value that fails to parse
/// as absent (the condition is ignored) rather than as an error — hence the <c>TryParse</c> shape and
/// the absence of a throwing <c>Parse</c> overload.
/// </para>
/// </remarks>
public static class HttpDate
{
    // RFC 9110 §5.6.7 accepts three date formats. The framework has no space-padded-day specifier,
    // so the asctime form (single-digit days are space-padded to two columns) is covered by two
    // explicit patterns: a double-space form for days 1–9 and a single-space form for days 10–31.
    private static readonly string[] AcceptedFormats =
    [
        "ddd, dd MMM yyyy HH:mm:ss 'GMT'",   // IMF-fixdate (preferred)
        "dddd, dd'-'MMM'-'yy HH:mm:ss 'GMT'", // RFC 850 (obsolete)
        "ddd MMM d HH:mm:ss yyyy",            // asctime, days 10–31 / single-space day
        "ddd MMM  d HH:mm:ss yyyy",           // asctime, space-padded single-digit day
    ];

    private const DateTimeStyles ParseStyles =
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces;

    /// <summary>
    /// Attempts to parse <paramref name="value"/> as an HTTP-date in any of the three RFC 9110
    /// &#167; 5.6.7 forms.
    /// </summary>
    /// <param name="value">The candidate HTTP-date text.</param>
    /// <param name="date">
    /// When this method returns <see langword="true"/>, the parsed timestamp as a UTC
    /// <see cref="DateTimeOffset"/> (zero offset); otherwise <see cref="DateTimeOffset.MinValue"/>.
    /// </param>
    /// <returns><see langword="true"/> when the value is a well-formed HTTP-date.</returns>
    public static bool TryParse(ReadOnlySpan<char> value, out DateTimeOffset date)
    {
        ReadOnlySpan<char> trimmed = value.Trim();
        if (trimmed.IsEmpty)
        {
            date = default;
            return false;
        }

        if (DateTimeOffset.TryParseExact(trimmed, AcceptedFormats, CultureInfo.InvariantCulture, ParseStyles, out DateTimeOffset parsed))
        {
            date = parsed;
            return true;
        }

        date = default;
        return false;
    }

    /// <summary>
    /// Attempts to parse <paramref name="value"/> as an HTTP-date in any of the three RFC 9110
    /// &#167; 5.6.7 forms.
    /// </summary>
    /// <param name="value">The candidate HTTP-date text.</param>
    /// <param name="date">
    /// When this method returns <see langword="true"/>, the parsed timestamp as a UTC
    /// <see cref="DateTimeOffset"/> (zero offset); otherwise <see cref="DateTimeOffset.MinValue"/>.
    /// </param>
    /// <returns><see langword="true"/> when the value is a well-formed HTTP-date.</returns>
    public static bool TryParse(string? value, out DateTimeOffset date)
        => TryParse(value.AsSpan(), out date);

    /// <summary>
    /// Formats <paramref name="date"/> as an IMF-fixdate HTTP-date (RFC 9110 &#167; 5.6.7), the
    /// form a sender is required to emit (e.g. <c>Sun, 06 Nov 1994 08:49:37 GMT</c>).
    /// </summary>
    /// <param name="date">The timestamp to format; it is converted to UTC before formatting.</param>
    /// <returns>The IMF-fixdate representation.</returns>
    public static string Format(DateTimeOffset date)
        => date.ToUniversalTime().ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture);
}
