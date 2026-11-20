using System;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The <c>date-time</c>, <c>date</c> and <c>time</c> formats: RFC 3339 §5.6 <c>date-time</c>,
/// <c>full-date</c> and <c>full-time</c>.
/// </summary>
/// <remarks>
/// Digits are ASCII only, <c>T</c> and <c>Z</c> may be lower case (RFC 3339 §5.6 note), a second
/// fraction has one or more digits, and an offset needs both hours and minutes. Day numbers follow the
/// month and the Gregorian leap-year rule. A leap second (<c>:60</c>) is valid only where the time,
/// moved to UTC by its offset, is 23:59 (RFC 3339 §5.7); which dates carry one is not checked.
/// </remarks>
internal static class DateTimeFormat
{
    private const int minutesPerDay = 24 * 60;
    private const int lastMinuteOfDay = (23 * 60) + 59;

    /// <summary>RFC 3339 <c>date-time</c>: <c>full-date "T" full-time</c>.</summary>
    public static bool IsDateTime(ReadOnlySpan<char> value, JsonSchemaVersion version) =>
        value.Length > 10
        && IsFullDate(value.Slice(0, 10))
        && value[10] is 'T' or 't'
        && IsFullTime(value.Slice(11));

    /// <summary>RFC 3339 <c>full-date</c>.</summary>
    public static bool IsDate(ReadOnlySpan<char> value, JsonSchemaVersion version) => IsFullDate(value);

    /// <summary>RFC 3339 <c>full-time</c>: a partial time and an offset.</summary>
    public static bool IsTime(ReadOnlySpan<char> value, JsonSchemaVersion version) => IsFullTime(value);

    private static bool IsFullDate(ReadOnlySpan<char> value)
    {
        if (value.Length != 10
            || !TryReadDigits(value, 0, 4, out int year)
            || value[4] != '-'
            || !TryReadDigits(value, 5, 2, out int month)
            || value[7] != '-'
            || !TryReadDigits(value, 8, 2, out int day))
        {
            return false;
        }

        return month is >= 1 and <= 12 && day >= 1 && day <= DaysInMonth(year, month);
    }

    private static bool IsFullTime(ReadOnlySpan<char> value)
    {
        // time-hour ":" time-minute ":" time-second [time-secfrac] time-offset
        if (value.Length < 9
            || !TryReadDigits(value, 0, 2, out int hour)
            || value[2] != ':'
            || !TryReadDigits(value, 3, 2, out int minute)
            || value[5] != ':'
            || !TryReadDigits(value, 6, 2, out int second)
            || hour > 23
            || minute > 59
            || second > 60)
        {
            return false;
        }

        int position = 8;
        if (value[position] == '.')
        {
            int start = ++position;
            while (position < value.Length && char.IsAsciiDigit(value[position]))
            {
                position++;
            }

            if (position == start)
            {
                return false;
            }
        }

        if (position >= value.Length)
        {
            return false;
        }

        int offset;
        char indicator = value[position];
        if (indicator is 'Z' or 'z')
        {
            offset = 0;
            position++;
        }
        else if (indicator is '+' or '-')
        {
            if (value.Length - position != 6
                || !TryReadDigits(value, position + 1, 2, out int offsetHour)
                || value[position + 3] != ':'
                || !TryReadDigits(value, position + 4, 2, out int offsetMinute)
                || offsetHour > 23
                || offsetMinute > 59)
            {
                return false;
            }

            offset = ((offsetHour * 60) + offsetMinute) * (indicator == '-' ? -1 : 1);
            position += 6;
        }
        else
        {
            return false;
        }

        if (position != value.Length)
        {
            return false;
        }

        if (second == 60)
        {
            int utc = ((((hour * 60) + minute - offset) % minutesPerDay) + minutesPerDay) % minutesPerDay;
            return utc == lastMinuteOfDay;
        }

        return true;
    }

    private static bool TryReadDigits(ReadOnlySpan<char> value, int start, int count, out int number)
    {
        number = 0;
        for (int i = start; i < start + count; i++)
        {
            char c = value[i];
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }

            number = (number * 10) + (c - '0');
        }

        return true;
    }

    private static int DaysInMonth(int year, int month) => month switch
    {
        2 => year % 4 == 0 && (year % 100 != 0 || year % 400 == 0) ? 29 : 28,
        4 or 6 or 9 or 11 => 30,
        _ => 31,
    };
}
