using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Sql.Language.Internal;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Translates the shared temporal reader's format/range distinction into execution diagnostics.</summary>
internal static class SqlTemporalText
{
    private static readonly SqlTemporalReader _reader = new(Message);

    internal static DateOnly ParseDate(string text)
    {
        try { return _reader.ParseDate(text); }
        catch (SqlTemporalReadException failure) { throw Failure(failure); }
    }

    internal static TimeOnly ParseTime(string text)
    {
        try { return _reader.ParseTime(text); }
        catch (SqlTemporalReadException failure) { throw Failure(failure); }
    }

    internal static DateTime ParseTimestamp(string text)
    {
        try { return _reader.ParseTimestamp(text); }
        catch (SqlTemporalReadException failure) { throw Failure(failure); }
    }

    internal static DateTimeOffset ParseTimestampWithTimeZone(string text)
    {
        try { return _reader.ParseTimestampWithTimeZone(text); }
        catch (SqlTemporalReadException failure) { throw Failure(failure); }
    }

    internal static TimeSpan ParseInterval(string text)
    {
        try { return _reader.ParseInterval(text); }
        catch (SqlTemporalReadException failure) { throw Failure(failure); }
    }

    // Assignment retains its existing permissive date/timestamp spellings. A reader's
    // field-range failure is final; only nonstandard grammar tries the established fallback.
    internal static DateOnly ParseAssignmentDate(string text)
    {
        RequireYearFirst(text);
        try { return _reader.ParseDate(text.Trim()); }
        catch (SqlTemporalReadException failure)
        {
            if (failure.Kind == SqlTemporalFailureKind.Format && DateOnly.TryParse(text, CultureInfo.InvariantCulture, out var date)) { return date; }
            throw Failure(failure);
        }
    }

    internal static DateTime ParseAssignmentTimestamp(string text) => ParseAssignmentOffset(text).DateTime;
    internal static DateTimeOffset ParseAssignmentTimestampTz(string text) => ParseAssignmentOffset(text);

    private static DateTimeOffset ParseAssignmentOffset(string text)
    {
        RequireYearFirst(text);
        try { return _reader.ParseTimestampWithTimeZone(text.Trim()); }
        catch (SqlTemporalReadException failure)
        {
            if (failure.Kind == SqlTemporalFailureKind.Format && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var timestamp)) { return timestamp; }
            throw Failure(failure);
        }
    }

    private static void RequireYearFirst(string text)
    {
        var span = text.AsSpan().TrimStart();
        if (span.Length < 5 || !char.IsAsciiDigit(span[0]) || !char.IsAsciiDigit(span[1]) || !char.IsAsciiDigit(span[2])
            || !char.IsAsciiDigit(span[3]) || span[4] != '-')
        { throw new SqlExecutionException(SqlExecutionException.InvalidDatetimeCode, ErrorMessages.TemporalDateTextInvalid); }
    }

    private static SqlExecutionException Failure(SqlTemporalReadException failure)
        => new(failure.Kind switch
        {
            SqlTemporalFailureKind.FieldRange => SqlExecutionException.DatetimeOverflowCode,
            SqlTemporalFailureKind.IntervalOverflow => SqlExecutionException.IntervalOverflowCode,
            _ => SqlExecutionException.InvalidDatetimeCode,
        }, failure.Message, failure.Kind switch
        {
            SqlTemporalFailureKind.FieldRange or SqlTemporalFailureKind.IntervalOverflow => new OverflowException(failure.Message, failure.InnerException),
            SqlTemporalFailureKind.UnsupportedYearMonth => new NotSupportedException(failure.Message, failure.InnerException),
            _ => new FormatException(failure.Message, failure.InnerException),
        });

    private static string Message(SqlTemporalTextError error) => error switch
    {
        SqlTemporalTextError.DateTextInvalid => ErrorMessages.TemporalDateTextInvalid,
        SqlTemporalTextError.TimestampTextInvalid => ErrorMessages.TemporalTimestampTextInvalid,
        SqlTemporalTextError.TimestampSeparatorInvalid => ErrorMessages.TemporalTimestampSeparatorInvalid,
        SqlTemporalTextError.TimestampZoneRequired => ErrorMessages.TemporalTimestampZoneRequired,
        SqlTemporalTextError.TimestampOffsetInvalid => ErrorMessages.TemporalTimestampOffsetInvalid,
        SqlTemporalTextError.TimestampOffsetRange => ErrorMessages.TemporalTimestampOffsetRange,
        SqlTemporalTextError.TimestampRangeInvalid => ErrorMessages.TemporalTimestampRangeInvalid,
        SqlTemporalTextError.IntervalYearMonthUnsupported => ErrorMessages.TemporalIntervalYearMonthUnsupported,
        SqlTemporalTextError.TimeTextInvalid => ErrorMessages.TemporalTimeTextInvalid,
        SqlTemporalTextError.IntervalTextInvalid => ErrorMessages.TemporalIntervalTextInvalid,
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };
}
