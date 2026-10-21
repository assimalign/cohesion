using System;

using Assimalign.Cohesion.Database.Sql.Language.Internal;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Reads SQL literal text using strict, invariant grammars independent of the host clock and time zone.</summary>
/// <remarks>Years have four digits, fractions have at most seven digits, and intervals contain day-time fields only.</remarks>
public static class SqlLiteralText
{
    private static readonly SqlTemporalReader _reader = new(Message);

    /// <summary>Reads a DATE in yyyy-MM-dd form.</summary>
    /// <param name="text">The unquoted text.</param>
    /// <returns>The date.</returns>
    /// <exception cref="FormatException">The text is not a valid date.</exception>
    /// <exception cref="OverflowException">A calendar, clock or offset field is outside the supported range.</exception>
    public static DateOnly ParseDate(string text)
    {
        try { return _reader.ParseDate(text); }
        catch (SqlTemporalReadException failure) { throw Translate(failure); }
    }

    /// <summary>Reads a TIME with optional AM/PM and a discarded numeric zone or common zone abbreviation.</summary>
    /// <param name="text">The unquoted text.</param>
    /// <returns>The time.</returns>
    /// <exception cref="FormatException">The text is not a valid time.</exception>
    /// <exception cref="OverflowException">A calendar, clock or offset field is outside the supported range.</exception>
    public static TimeOnly ParseTime(string text)
    {
        try { return _reader.ParseTime(text); }
        catch (SqlTemporalReadException failure) { throw Translate(failure); }
    }

    /// <summary>Reads a zone-less TIMESTAMP: yyyy-MM-dd[ T HH:mm[:ss[.fffffff]]] (one space or T separator).</summary>
    /// <param name="text">The unquoted text.</param>
    /// <returns>The wall clock with Unspecified kind.</returns>
    /// <exception cref="FormatException">The text is malformed or contains a zone.</exception>
    /// <exception cref="OverflowException">A calendar, clock or offset field is outside the supported range.</exception>
    public static DateTime ParseTimestamp(string text)
    {
        try { return _reader.ParseTimestamp(text); }
        catch (SqlTemporalReadException failure) { throw Translate(failure); }
    }

    /// <summary>Reads a TIMESTAMPTZ with optional Z or +/-HH:mm offset; no offset means UTC.</summary>
    /// <param name="text">The unquoted text.</param>
    /// <returns>The instant retaining its written offset.</returns>
    /// <exception cref="FormatException">The timestamp or offset is malformed.</exception>
    /// <exception cref="OverflowException">A calendar, clock or offset field is outside the supported range.</exception>
    public static DateTimeOffset ParseTimestampWithTimeZone(string text)
    {
        try { return _reader.ParseTimestampWithTimeZone(text); }
        catch (SqlTemporalReadException failure) { throw Translate(failure); }
    }

    /// <summary>Reads a day-time interval: a qualified ISO field value, PostgreSQL unit/clock string, bare seconds, or ISO 8601 duration.</summary>
    /// <param name="text">The unquoted text.</param>
    /// <param name="qualifier">An optional DAY, HOUR, MINUTE or SECOND field or descending TO range.</param>
    /// <returns>The exact duration, at 100 nanosecond resolution.</returns>
    /// <exception cref="FormatException">The grammar is invalid or has more than seven fractional digits.</exception>
    /// <exception cref="OverflowException">The duration does not fit TimeSpan.</exception>
    /// <exception cref="NotSupportedException">A year-month field is present.</exception>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    public static TimeSpan ParseInterval(string text, string? qualifier = null)
    {
        try { return _reader.ParseInterval(text, qualifier); }
        catch (SqlTemporalReadException failure) { throw Translate(failure); }
    }

    /// <summary>Reads an even number of hexadecimal digits, including empty text.</summary>
    /// <param name="text">The unquoted text of X'...'.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="FormatException">The text has an odd length or contains a non-hexadecimal character.</exception>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    public static byte[] ParseBinary(string text) => Convert.FromHexString(text);

    private static Exception Translate(SqlTemporalReadException failure) => failure.Kind switch
    {
        SqlTemporalFailureKind.FieldRange or SqlTemporalFailureKind.IntervalOverflow => new OverflowException(failure.Message, failure.InnerException),
        SqlTemporalFailureKind.UnsupportedYearMonth => new NotSupportedException(failure.Message, failure.InnerException),
        _ => new FormatException(failure.Message, failure.InnerException),
    };

    private static string Message(SqlTemporalTextError error) => error switch
    {
        SqlTemporalTextError.DateTextInvalid => ErrorMessages.DateTextInvalid,
        SqlTemporalTextError.TimestampTextInvalid => ErrorMessages.TimestampTextInvalid,
        SqlTemporalTextError.TimestampSeparatorInvalid => ErrorMessages.TimestampSeparatorInvalid,
        SqlTemporalTextError.TimestampZoneRequired => ErrorMessages.TimestampZoneRequired,
        SqlTemporalTextError.TimestampOffsetInvalid => ErrorMessages.TimestampOffsetInvalid,
        SqlTemporalTextError.TimestampOffsetRange => ErrorMessages.TimestampOffsetRange,
        SqlTemporalTextError.TimestampRangeInvalid => ErrorMessages.TimestampRangeInvalid,
        SqlTemporalTextError.IntervalYearMonthUnsupported => ErrorMessages.IntervalYearMonthUnsupported,
        SqlTemporalTextError.TimeTextInvalid => ErrorMessages.TimeTextInvalid,
        SqlTemporalTextError.IntervalTextInvalid => ErrorMessages.IntervalTextInvalid,
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };
}
