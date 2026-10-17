using System;
using System.Globalization;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Reads SQL literal text using strict, invariant grammars independent of the host clock and time zone.</summary>
/// <remarks>Years have four digits, fractions have at most seven digits, and intervals contain day-time fields only.</remarks>
public static class SqlLiteralText
{
    /// <summary>Reads a DATE in yyyy-MM-dd form.</summary>
    /// <param name="text">The unquoted text.</param>
    /// <returns>The date.</returns>
    /// <exception cref="FormatException">The text is not a valid date.</exception>
    public static DateOnly ParseDate(string text)
    {
        if (text is null || text.Length != 10 || !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new FormatException("DATE text must be a valid ISO 8601 date, yyyy-mm-dd.");
        }
        return date;
    }

    /// <summary>Reads a TIME in HH:mm[:ss[.fffffff]] form, without a zone.</summary>
    /// <param name="text">The unquoted text.</param>
    /// <returns>The time.</returns>
    /// <exception cref="FormatException">The text is not a valid time.</exception>
    public static TimeOnly ParseTime(string text)
    {
        ReadOnlySpan<char> span = text.AsSpan();
        if (span.Length < 5 || span[2] != ':' || !Digits(span[..2]) || !Digits(span.Slice(3, 2))) { throw InvalidTime(); }
        int hour = Number(span[..2]), minute = Number(span.Slice(3, 2)), second = 0;
        long fraction = 0;
        if (span.Length > 5)
        {
            if (span.Length < 8 || span[5] != ':' || !Digits(span.Slice(6, 2))) { throw InvalidTime(); }
            second = Number(span.Slice(6, 2));
            if (span.Length > 8)
            {
                if (span[8] != '.' || span.Length is < 10 or > 16 || !Digits(span[9..])) { throw InvalidTime(); }
                fraction = Number(span[9..]);
                for (int i = span.Length - 9; i < 7; i++) { fraction *= 10; }
            }
        }
        if (hour > 23 || minute > 59 || second > 59) { throw InvalidTime(); }
        return new TimeOnly((hour * 3600L + minute * 60L + second) * TimeSpan.TicksPerSecond + fraction);
    }

    /// <summary>Reads a zone-less TIMESTAMP: yyyy-MM-dd[ T HH:mm[:ss[.fffffff]]] (one space or T separator).</summary>
    /// <param name="text">The unquoted text.</param>
    /// <returns>The wall clock with Unspecified kind.</returns>
    /// <exception cref="FormatException">The text is malformed or contains a zone.</exception>
    public static DateTime ParseTimestamp(string text)
    {
        if (text is null || text.Length < 10) { throw new FormatException("TIMESTAMP text requires yyyy-mm-dd and an optional time."); }
        var date = ParseDate(text[..10]);
        if (text.Length == 10) { return date.ToDateTime(TimeOnly.MinValue); }
        if (text[10] is not (' ' or 'T')) { throw new FormatException("TIMESTAMP requires a space or T before the time."); }
        return date.ToDateTime(ParseTime(text[11..]));
    }

    /// <summary>Reads a TIMESTAMPTZ with optional Z or +/-HH:mm offset; no offset means UTC.</summary>
    /// <param name="text">The unquoted text.</param>
    /// <returns>The instant retaining its written offset.</returns>
    /// <exception cref="FormatException">The timestamp or offset is malformed or outside the supported range.</exception>
    public static DateTimeOffset ParseTimestampWithTimeZone(string text)
    {
        if (text is null) { throw new FormatException("TIMESTAMPTZ requires timestamp text."); }
        int end = text.Length;
        TimeSpan offset = TimeSpan.Zero;
        if (end > 10 && text[^1] == 'Z') { end--; }
        else if (end >= 16 && text[^6] is '+' or '-')
        {
            var zone = text.AsSpan(end - 6);
            if (zone[3] != ':' || !Digits(zone.Slice(1, 2)) || !Digits(zone.Slice(4, 2))) { throw new FormatException("An offset must be +/-HH:mm."); }
            int hours = Number(zone.Slice(1, 2)), minutes = Number(zone.Slice(4, 2));
            if (hours > 14 || minutes > 59 || hours == 14 && minutes != 0) { throw new FormatException("An offset must be within +/-14:00."); }
            offset = TimeSpan.FromMinutes((hours * 60 + minutes) * (zone[0] == '-' ? -1 : 1));
            end -= 6;
        }
        try { return new DateTimeOffset(ParseTimestamp(text[..end]), offset); }
        catch (ArgumentException exception) { throw new FormatException("TIMESTAMPTZ is outside its supported range.", exception); }
    }

    /// <summary>Reads an even number of hexadecimal digits, including empty text.</summary>
    /// <param name="text">The unquoted text of X'...'.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="FormatException">The text has an odd length or contains a non-hexadecimal character.</exception>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    public static byte[] ParseBinary(string text) => Convert.FromHexString(text);

    /// <summary>Reads a day-time interval: a qualified ISO field value, PostgreSQL unit string, or ISO 8601 duration.</summary>
    /// <param name="text">The unquoted text.</param>
    /// <param name="qualifier">An optional DAY, HOUR, MINUTE or SECOND field or descending TO range.</param>
    /// <returns>The exact duration, at 100 nanosecond resolution.</returns>
    /// <exception cref="FormatException">The grammar is invalid or has more than seven fractional digits.</exception>
    /// <exception cref="OverflowException">The duration does not fit TimeSpan.</exception>
    /// <exception cref="NotSupportedException">A year-month field is present.</exception>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    public static TimeSpan ParseInterval(string text, string? qualifier = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        string value = text.Trim();
        decimal ticks;
        if (qualifier is not null)
        {
            string[] fields = qualifier.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (string field in fields) { RejectYearMonth(field); }
            int first = fields.Length > 0 ? Field(fields[0]) : -1;
            int last = fields.Length == 1 ? first : fields.Length == 3 && fields[1] == "TO" ? Field(fields[2]) : -1;
            if (first < 0 || last < first || last < 0 || fields.Length == 3 && last == first) { throw InvalidInterval(); }
            ticks = Qualified(value, first, last);
        }
        else
        {
            int sign = 1;
            ReadOnlySpan<char> span = value.AsSpan();
            if (!span.IsEmpty && span[0] is '+' or '-') { sign = span[0] == '-' ? -1 : 1; span = span[1..]; }
            ticks = !span.IsEmpty && span[0] == 'P' ? IsoInterval(span) * sign : UnitInterval(value);
        }
        if (ticks != decimal.Truncate(ticks)) { throw InvalidInterval(); }
        return new TimeSpan(checked((long)ticks));
    }

    private static decimal Qualified(string text, int first, int last)
    {
        int sign = 1;
        if (text.Length > 0 && text[0] is '+' or '-') { sign = text[0] == '-' ? -1 : 1; text = text[1..]; }
        string[] parts = text.Split([' ', ':'], StringSplitOptions.None);
        if (parts.Length != last - first + 1) { throw InvalidInterval(); }
        decimal ticks = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            decimal number = IntervalNumber(parts[i].AsSpan(), allowSign: false, fraction: first + i == 3);
            if (i > 0 && number >= (first + i == 1 ? 24 : 60)) { throw InvalidInterval(); }
            // DAY ranges separate their day and clock by a space; clock fields use colons.
            ticks += number * FieldTicks(first + i);
        }
        string separators = first == 0 && last > 0 ? " " + new string(':', last - first - 1) : new string(':', last - first);
        int boundary = 0;
        foreach (char character in text)
        {
            if (character is ' ' or ':')
            {
                if (boundary >= separators.Length || character != separators[boundary++]) { throw InvalidInterval(); }
            }
        }
        return ticks * sign;
    }

    private static decimal IsoInterval(ReadOnlySpan<char> text)
    {
        // PostgreSQL datetime.c:3959-3977 also distinguishes date M (months) from time M (minutes).
        bool time = false, any = false, timeValue = false;
        int previous = -1, position = 1;
        decimal ticks = 0;
        while (position < text.Length)
        {
            if (text[position] == 'T')
            {
                if (time) { throw InvalidInterval(); }
                time = true; position++; continue;
            }
            int start = position;
            while (position < text.Length && (char.IsAsciiDigit(text[position]) || text[position] == '.')) { position++; }
            if (position == start || position == text.Length) { throw InvalidInterval(); }
            char unit = text[position++];
            if (unit == 'Y' || unit == 'M' && !time) { throw new NotSupportedException("Year-month INTERVAL literals are not supported."); }
            if (unit == 'W' && !time)
            {
                if (any || position != text.Length) { throw InvalidInterval(); }
                return IntervalNumber(text[start..(position - 1)], false, true) * 7 * TimeSpan.TicksPerDay;
            }
            int field = unit switch { 'D' when !time => 0, 'H' when time => 1, 'M' when time => 2, 'S' when time => 3, _ => -1 };
            if (field < 0 || field <= previous) { throw InvalidInterval(); }
            ticks += IntervalNumber(text[start..(position - 1)], false, true) * FieldTicks(field);
            any = true; timeValue |= time; previous = field;
        }
        if (!any || time && !timeValue) { throw InvalidInterval(); }
        return ticks;
    }

    private static decimal UnitInterval(string text)
    {
        ReadOnlySpan<char> span = text.AsSpan();
        int position = 0;
        decimal ticks = 0;
        bool any = false;
        int seen = 0;
        while (position < span.Length)
        {
            while (position < span.Length && span[position] == ' ') { position++; }
            int start = position;
            if (position < span.Length && span[position] is '+' or '-') { position++; }
            while (position < span.Length && (char.IsAsciiDigit(span[position]) || span[position] == '.')) { position++; }
            int numberStart = start, numberEnd = position;
            while (position < span.Length && span[position] == ' ') { position++; }
            start = position;
            while (position < span.Length && char.IsAsciiLetter(span[position])) { position++; }
            string unit = span[start..position].ToString().ToUpperInvariant();
            RejectYearMonth(unit);
            decimal number = IntervalNumber(span[numberStart..numberEnd], true, true);
            int field = unit switch
            {
                "D" or "DAY" or "DAYS" => 0,
                "H" or "HOUR" or "HOURS" or "HR" or "HRS" => 1,
                "M" or "MINUTE" or "MINUTES" or "MIN" or "MINS" => 2,
                "S" or "SECOND" or "SECONDS" or "SEC" or "SECS" => 3,
                "W" or "WEEK" or "WEEKS" => 4,
                "MILLISECOND" or "MILLISECONDS" or "MS" or "MSEC" or "MSECS" or "MSECOND" or "MSECONDS" => 5,
                "MICROSECOND" or "MICROSECONDS" or "US" or "USEC" or "USECS" or "USECOND" or "USECONDS" => 6,
                _ => -1,
            };
            if (field < 0 || (seen & (1 << field)) != 0 || position < span.Length && span[position] != ' ') { throw InvalidInterval(); }
            ticks += number * FieldTicks(field);
            seen |= 1 << field; any = true;
        }
        if (!any) { throw InvalidInterval(); }
        return ticks;
    }

    private static decimal IntervalNumber(ReadOnlySpan<char> text, bool allowSign, bool fraction)
    {
        var digits = text;
        if (allowSign && !digits.IsEmpty && digits[0] is '+' or '-') { digits = digits[1..]; }
        int dot = digits.IndexOf('.');
        if (dot >= 0)
        {
            if (!fraction || dot == 0 || digits.Length - dot - 1 is < 1 or > 7 || !Digits(digits[..dot]) || !Digits(digits[(dot + 1)..])) { throw InvalidInterval(); }
        }
        else if (!Digits(digits)) { throw InvalidInterval(); }
        return decimal.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }

    private static int Field(string field) => field switch { "DAY" => 0, "HOUR" => 1, "MINUTE" => 2, "SECOND" => 3, _ => -1 };
    private static long FieldTicks(int field) => field switch
    {
        0 => TimeSpan.TicksPerDay, 1 => TimeSpan.TicksPerHour, 2 => TimeSpan.TicksPerMinute,
        4 => 7 * TimeSpan.TicksPerDay, 5 => TimeSpan.TicksPerMillisecond, 6 => TimeSpan.TicksPerMicrosecond,
        _ => TimeSpan.TicksPerSecond,
    };
    private static void RejectYearMonth(string field)
    {
        if (field is "Y" or "YEAR" or "YEARS" or "YR" or "YRS" or "MONTH" or "MONTHS" or "MON" or "MONS"
            or "DECADE" or "DECADES" or "DEC" or "DECS" or "CENTURY" or "CENTURIES" or "CENT" or "C"
            or "MILLENNIUM" or "MILLENNIA" or "MIL" or "MILS" or "QUARTER" or "QTR") { throw new NotSupportedException("Year-month INTERVAL literals are not supported."); }
    }
    private static bool Digits(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) { return false; }
        foreach (char character in text) { if (!char.IsAsciiDigit(character)) { return false; } }
        return true;
    }
    private static int Number(ReadOnlySpan<char> text) => int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
    private static FormatException InvalidTime() => new("TIME requires HH:mm[:ss[.fffffff]], within 00:00:00 through 23:59:59.9999999, without a zone.");
    private static FormatException InvalidInterval() => new("Invalid day-time INTERVAL literal.");
}
