using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Reads text as a date, a timestamp or a timestamp with an offset, one reading for an INSERT into a
/// column of the type (<see cref="SqlPlanExecutor.CoerceForColumn"/>) and for <c>CAST</c>
/// (<see cref="SqlStandardCasts"/>), so neither depends on the host's clock or time zone.
/// </summary>
/// <remarks>
/// <para>
/// The text starts with an ISO 8601 date, <c>yyyy-mm-dd</c>: a text without a year (<c>'1.5'</c>)
/// would otherwise take the current year from the clock. A timestamp's time of day and offset follow
/// as <see cref="DateTimeOffset.Parse(string, IFormatProvider, DateTimeStyles)"/> reads them in the
/// invariant culture.
/// </para>
/// <para>
/// <c>TIMESTAMP</c> keeps the clock time as written and ignores an offset, as PostgreSQL's
/// <c>timestamp_in</c> does (<c>src/backend/utils/adt/timestamp.c</c>): the result's kind is
/// <see cref="DateTimeKind.Unspecified"/>. <c>TIMESTAMPTZ</c> keeps a written offset and takes UTC
/// for text without one: the engine has no session time zone, and the host's is not the database's.
/// </para>
/// <para>
/// Every failure is a <see cref="FormatException"/> or an <see cref="OverflowException"/>, the value
/// faults the cast registry reports as conversion failures.
/// </para>
/// </remarks>
internal static class SqlTemporalText
{
    /// <summary>Reads a <c>DATE</c>.</summary>
    /// <param name="text">The text, <c>yyyy-mm-dd</c>.</param>
    /// <returns>The date.</returns>
    /// <exception cref="FormatException">The text is not a date that starts with its year.</exception>
    internal static DateOnly ParseDate(string text)
    {
        RequireYearFirst(text, "DATE");
        return SqlLiteralText.ParseDate(DateOnly.Parse(text, CultureInfo.InvariantCulture).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    /// <summary>Reads a <c>TIMESTAMP</c>: the clock time as written, any offset ignored.</summary>
    /// <param name="text">The text, <c>yyyy-mm-dd[Thh:mm[:ss[.fffffff]]][offset]</c>.</param>
    /// <returns>The timestamp, of kind <see cref="DateTimeKind.Unspecified"/>.</returns>
    /// <exception cref="FormatException">The text is not a timestamp that starts with its year.</exception>
    /// <exception cref="OverflowException">The timestamp is outside the range the type holds.</exception>
    internal static DateTime ParseTimestamp(string text) => ParseOffset(text, "TIMESTAMP").DateTime;

    /// <summary>Reads a <c>TIMESTAMPTZ</c>: the written offset, or UTC when none is written.</summary>
    /// <param name="text">The text, <c>yyyy-mm-dd[Thh:mm[:ss[.fffffff]]][offset]</c>.</param>
    /// <returns>The timestamp with its offset.</returns>
    /// <exception cref="FormatException">The text is not a timestamp that starts with its year.</exception>
    /// <exception cref="OverflowException">The timestamp is outside the range the type holds.</exception>
    internal static DateTimeOffset ParseTimestampTz(string text) => ParseOffset(text, "TIMESTAMPTZ");

    private static DateTimeOffset ParseOffset(string text, string type)
    {
        RequireYearFirst(text, type);
        try
        {
            // Plain-string assignment keeps its existing input acceptance. Normalize that input
            // before delegating to the literal grammar; typed literals call the strict reader directly.
            var value = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            return SqlLiteralText.ParseTimestampWithTimeZone(value.ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            // An offset that moves the instant past the type's range.
            throw new OverflowException($"{type} text '{text}' is outside the range of the type.", exception);
        }
    }

    private static void RequireYearFirst(string text, string type)
    {
        var span = text.AsSpan().TrimStart();
        if (span.Length < 5 || !char.IsAsciiDigit(span[0]) || !char.IsAsciiDigit(span[1]) || !char.IsAsciiDigit(span[2])
            || !char.IsAsciiDigit(span[3]) || span[4] != '-')
        {
            throw new FormatException($"{type} text must start with an ISO 8601 date, yyyy-mm-dd.");
        }
    }
}
