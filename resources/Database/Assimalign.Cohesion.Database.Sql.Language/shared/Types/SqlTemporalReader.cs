using System;
using System.Globalization;

// Deviates from the repo namespace-matches-assembly rule: stateless parsing is linked into SQL and Language; reader instances and failures never cross assemblies.
namespace Assimalign.Cohesion.Database.Sql.Language.Internal;

/// <summary>Reads temporal fields and records whether grammar, field range or duration overflow failed.</summary>
internal sealed class SqlTemporalReader
{
    private readonly Func<SqlTemporalTextError, string> _message;

    internal SqlTemporalReader(Func<SqlTemporalTextError, string> message)
    {
        _message = message;
    }

    /// <summary>Reads a DATE in yyyy-MM-dd form.</summary>
    /// <param name="text">The unquoted text.</param>
    /// <returns>The date.</returns>
    /// <exception cref="FormatException">The text is not a valid date.</exception>
    internal DateOnly ParseDate(string text)
    {
        if (text is null || text.Length != 10 || text[4] != '-' || text[7] != '-'
            || !Digits(text.AsSpan(0, 4)) || !Digits(text.AsSpan(5, 2)) || !Digits(text.AsSpan(8, 2)))
        {
            throw Failure(SqlTemporalFailureKind.Format, _message(SqlTemporalTextError.DateTextInvalid));
        }
        int year = Number(text.AsSpan(0, 4)), month = Number(text.AsSpan(5, 2)), day = Number(text.AsSpan(8, 2));
        if (year == 0 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            throw Failure(SqlTemporalFailureKind.FieldRange, _message(SqlTemporalTextError.DateTextInvalid));
        }
        return new DateOnly(year, month, day);
    }

    /// <summary>Reads a TIME with optional AM/PM and a discarded numeric zone or common zone abbreviation.</summary>
    /// <param name="text">The unquoted text.</param>
    /// <returns>The time.</returns>
    /// <exception cref="FormatException">The text is not a valid time.</exception>
    internal TimeOnly ParseTime(string text)
    {
        ReadOnlySpan<char> span = text.AsSpan();
        foreach (char character in span)
        {
            if (character > 127 && char.IsWhiteSpace(character)) { throw InvalidTime(); }
        }
        span = span.Trim();
        int end = 0;
        while (end < span.Length && (char.IsAsciiDigit(span[end]) || span[end] is ':' or '.')) { end++; }
        var clock = span[..end];
        var suffix = span[end..].Trim();
        int meridiem = -1;
        bool zone = false;
        while (!suffix.IsEmpty)
        {
            int length = 0;
            while (length < suffix.Length && !IsAsciiWhiteSpace(suffix[length])) { length++; }
            var token = suffix[..length];
            if (token.Equals("AM", StringComparison.OrdinalIgnoreCase) || token.Equals("PM", StringComparison.OrdinalIgnoreCase))
            {
                if (meridiem >= 0) { throw InvalidTime(); }
                meridiem = token.Equals("PM", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            }
            else
            {
                if (zone || !IsTimeZone(token)) { throw InvalidTime(); }
                zone = true;
            }
            suffix = suffix[length..].TrimStart();
        }
        int colon = clock.IndexOf(':');
        var hourText = colon < 0 ? clock : clock[..colon];
        if (hourText.Length is < 1 or > 2 || !Digits(hourText) || colon < 0 && meridiem < 0) { throw InvalidTime(); }
        int hour = Number(hourText), minute = 0, second = 0;
        long fraction = 0;
        if (colon >= 0)
        {
            var rest = clock[(colon + 1)..];
            int next = rest.IndexOf(':');
            var minuteText = next < 0 ? rest : rest[..next];
            if (minuteText.Length is < 1 or > 2 || !Digits(minuteText)) { throw InvalidTime(); }
            minute = Number(minuteText);
            if (next >= 0)
            {
                var seconds = rest[(next + 1)..];
                int dot = seconds.IndexOf('.');
                var secondText = dot < 0 ? seconds : seconds[..dot];
                if (secondText.Length is < 1 or > 2 || !Digits(secondText)) { throw InvalidTime(); }
                second = Number(secondText);
                if (dot >= 0)
                {
                    var digits = seconds[(dot + 1)..];
                    if (digits.Length > 7 || !digits.IsEmpty && !Digits(digits)) { throw InvalidTime(); }
                    fraction = digits.IsEmpty ? 0 : Number(digits);
                    for (int i = digits.Length; i < 7; i++) { fraction *= 10; }
                }
            }
        }
        // PostgreSQL datetime.c:2345-2350 applies AM/PM after reading the clock;
        // date.c:1475-1510 time_in validates a zone but omits it from tm2time.
        if (meridiem >= 0)
        {
            if (hour > 12) { throw Failure(SqlTemporalFailureKind.FieldRange, _message(SqlTemporalTextError.TimeTextInvalid)); }
            hour = hour % 12 + meridiem * 12;
        }
        if (hour > 23 || minute > 59 || second > 59) { throw Failure(SqlTemporalFailureKind.FieldRange, _message(SqlTemporalTextError.TimeTextInvalid)); }
        return new TimeOnly((hour * 3600L + minute * 60L + second) * TimeSpan.TicksPerSecond + fraction);
    }

    private bool IsTimeZone(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) { return false; }
        if (text[0] is not ('+' or '-'))
        {
            return text.ToString().ToUpperInvariant() is "Z" or "UT" or "UTC" or "GMT"
                or "EST" or "EDT" or "CST" or "CDT" or "MST" or "MDT" or "PST" or "PDT"
                or "CET" or "CEST" or "EET" or "EEST" or "JST";
        }
        var offset = text[1..];
        int hour = 0, minute = 0, second = 0;
        if (offset.Contains(':'))
        {
            int index = 0;
            foreach (var range in offset.Split(':'))
            {
                var field = offset[range];
                if (field.Length is < 1 or > 2 || !Digits(field) || index > 2) { return false; }
                int value = Number(field);
                if (index == 0) { hour = value; }
                else if (index == 1) { minute = value; }
                else { second = value; }
                index++;
            }
        }
        else
        {
            if (offset.Length is < 1 or > 6 || offset.Length == 3 || offset.Length == 5 || !Digits(offset)) { return false; }
            hour = Number(offset[..Math.Min(2, offset.Length)]);
            if (offset.Length >= 4) { minute = Number(offset.Slice(2, 2)); }
            if (offset.Length == 6) { second = Number(offset.Slice(4, 2)); }
        }
        if (hour > 15 || minute > 59 || second > 59) { throw Failure(SqlTemporalFailureKind.FieldRange, _message(SqlTemporalTextError.TimestampOffsetRange)); }
        return true;
    }

    private TimeOnly ParseTimestampTime(string text)
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
        if (hour > 23 || minute > 59 || second > 59) { throw Failure(SqlTemporalFailureKind.FieldRange, _message(SqlTemporalTextError.TimeTextInvalid)); }
        return new TimeOnly((hour * 3600L + minute * 60L + second) * TimeSpan.TicksPerSecond + fraction);
    }

    /// <summary>Reads a zone-less TIMESTAMP: yyyy-MM-dd[ T HH:mm[:ss[.fffffff]]] (one space or T separator).</summary>
    /// <param name="text">The unquoted text.</param>
    /// <returns>The wall clock with Unspecified kind.</returns>
    /// <exception cref="FormatException">The text is malformed or contains a zone.</exception>
    internal DateTime ParseTimestamp(string text)
    {
        if (text is null || text.Length < 10) { throw Failure(SqlTemporalFailureKind.Format, _message(SqlTemporalTextError.TimestampTextInvalid)); }
        var date = ParseDate(text[..10]);
        if (text.Length == 10) { return date.ToDateTime(TimeOnly.MinValue); }
        if (text[10] is not (' ' or 'T')) { throw Failure(SqlTemporalFailureKind.Format, _message(SqlTemporalTextError.TimestampSeparatorInvalid)); }
        return date.ToDateTime(ParseTimestampTime(text[11..]));
    }

    /// <summary>Reads a TIMESTAMPTZ with optional Z or +/-HH:mm offset; no offset means UTC.</summary>
    /// <param name="text">The unquoted text.</param>
    /// <returns>The instant retaining its written offset.</returns>
    /// <exception cref="FormatException">The timestamp or offset is malformed or outside the supported range.</exception>
    internal DateTimeOffset ParseTimestampWithTimeZone(string text)
    {
        if (text is null) { throw Failure(SqlTemporalFailureKind.Format, _message(SqlTemporalTextError.TimestampZoneRequired)); }
        int end = text.Length;
        TimeSpan offset = TimeSpan.Zero;
        if (end > 10 && text[^1] == 'Z') { end--; }
        else if (end >= 16 && text[^6] is '+' or '-')
        {
            var zone = text.AsSpan(end - 6);
            if (zone[3] != ':' || !Digits(zone.Slice(1, 2)) || !Digits(zone.Slice(4, 2))) { throw Failure(SqlTemporalFailureKind.Format, _message(SqlTemporalTextError.TimestampOffsetInvalid)); }
            int hours = Number(zone.Slice(1, 2)), minutes = Number(zone.Slice(4, 2));
            if (hours > 14 || minutes > 59 || hours == 14 && minutes != 0) { throw Failure(SqlTemporalFailureKind.FieldRange, _message(SqlTemporalTextError.TimestampOffsetRange)); }
            offset = TimeSpan.FromMinutes((hours * 60 + minutes) * (zone[0] == '-' ? -1 : 1));
            end -= 6;
        }
        try { return new DateTimeOffset(ParseTimestamp(text[..end]), offset); }
        catch (ArgumentException exception) { throw Failure(SqlTemporalFailureKind.FieldRange, _message(SqlTemporalTextError.TimestampRangeInvalid), exception); }
    }

    /// <summary>Reads a day-time interval: a qualified ISO field value, PostgreSQL unit/clock string, bare seconds, or ISO 8601 duration.</summary>
    /// <param name="text">The unquoted text.</param>
    /// <param name="qualifier">An optional DAY, HOUR, MINUTE or SECOND field or descending TO range.</param>
    /// <returns>The exact duration, at 100 nanosecond resolution.</returns>
    /// <exception cref="FormatException">The grammar is invalid or has more than seven fractional digits.</exception>
    /// <exception cref="OverflowException">The duration does not fit TimeSpan.</exception>
    /// <exception cref="NotSupportedException">A year-month field is present.</exception>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    internal TimeSpan ParseInterval(string text, string? qualifier = null)
    {
        try { return ReadInterval(text, qualifier); }
        catch (OverflowException failure)
        {
            throw Failure(SqlTemporalFailureKind.IntervalOverflow, _message(SqlTemporalTextError.IntervalTextInvalid), failure);
        }
    }

    private TimeSpan ReadInterval(string text, string? qualifier)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (char character in text)
        {
            if (character > 127 && char.IsWhiteSpace(character)) { throw InvalidInterval(); }
        }
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

    private decimal Qualified(string text, int first, int last)
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

    private decimal IsoInterval(ReadOnlySpan<char> text)
    {
        // PostgreSQL datetime.c:4038-4108 recognizes basic and extended alternative dates.
        // These carry year/month fields even when their values happen to be zero.
        int dateEnd = text.IndexOf('T');
        ReadOnlySpan<char> date = dateEnd < 0 ? text[1..] : text[1..dateEnd];
        if (!date.IsEmpty && date.IndexOfAnyInRange('A', 'Z') < 0)
        {
            int datePosition = 0, fields = 0;
            while (datePosition < date.Length)
            {
                int start = datePosition;
                if (date[datePosition] == '-') { datePosition++; }
                while (datePosition < date.Length && (char.IsAsciiDigit(date[datePosition]) || date[datePosition] == '.')) { datePosition++; }
                _ = IntervalNumber(date[start..datePosition], true, true);
                if (++fields > 3) { throw InvalidInterval(); }
                if (datePosition == date.Length) { break; }
                if (date[datePosition++] != '-' || datePosition == date.Length) { throw InvalidInterval(); }
            }
            if (dateEnd >= 0)
            {
                var timeText = text[(dateEnd + 1)..];
                int timeFields = 0;
                foreach (var part in timeText.Split(':'))
                {
                    if (++timeFields > 3) { throw InvalidInterval(); }
                    _ = IntervalNumber(timeText[part], true, true);
                }
            }
            RejectYearMonth("YEAR");
        }
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
            if (text[position] == '-') { position++; }
            while (position < text.Length && (char.IsAsciiDigit(text[position]) || text[position] == '.')) { position++; }
            if (position == start || position == text.Length) { throw InvalidInterval(); }
            char unit = text[position++];
            if (unit == 'Y' || unit == 'M' && !time) { throw Failure(SqlTemporalFailureKind.UnsupportedYearMonth, _message(SqlTemporalTextError.IntervalYearMonthUnsupported)); }
            if (unit == 'W' && !time)
            {
                if (any || position != text.Length) { throw InvalidInterval(); }
                return IntervalNumber(text[start..(position - 1)], true, true) * 7 * TimeSpan.TicksPerDay;
            }
            int field = unit switch { 'D' when !time => 0, 'H' when time => 1, 'M' when time => 2, 'S' when time => 3, _ => -1 };
            if (field < 0 || field <= previous) { throw InvalidInterval(); }
            ticks += IntervalNumber(text[start..(position - 1)], true, true) * FieldTicks(field);
            any = true; timeValue |= time; previous = field;
        }
        if (!any || time && !timeValue) { throw InvalidInterval(); }
        return ticks;
    }

    private decimal UnitInterval(string text)
    {
        ReadOnlySpan<char> span = text.AsSpan();
        // PostgreSQL datetime.c:3807-3818 permits an optional @ and a final ago.
        if (!span.IsEmpty && span[0] == '@') { span = span[1..].TrimStart(); }
        bool ago = span.EndsWith("ago", StringComparison.OrdinalIgnoreCase);
        if (ago)
        {
            if (span.Length == 3 || !IsAsciiWhiteSpace(span[^4])) { throw InvalidInterval(); }
            span = span[..^3].TrimEnd();
        }
        int position = 0;
        decimal ticks = 0;
        bool any = false;
        int seen = 0;
        while (position < span.Length)
        {
            while (position < span.Length && IsAsciiWhiteSpace(span[position])) { position++; }
            if (position == span.Length) { break; }
            int start = position;
            if (position < span.Length && span[position] is '+' or '-') { position++; }
            while (position < span.Length && (char.IsAsciiDigit(span[position]) || span[position] == '.')) { position++; }
            int numberStart = start, numberEnd = position;
            if (position < span.Length && span[position] == ':')
            {
                // DecodeInterval sets the next implicit field to DAY after a clock, and keeps
                // the clock's sign independent of the day's (PostgreSQL datetime.c:3504-3596).
                while (position < span.Length && !IsAsciiWhiteSpace(span[position])) { position++; }
                const int clockFields = (1 << 1) | (1 << 2) | (1 << 3);
                if ((seen & clockFields) != 0) { throw InvalidInterval(); }
                ticks += IntervalClock(span[numberStart..position]);
                seen |= clockFields; any = true;
                continue;
            }
            if (position < span.Length && span[position] == '-')
            {
                // SQL years-months, including a leading sign (PostgreSQL datetime.c:3669-3695).
                int monthStart = ++position;
                while (position < span.Length && char.IsAsciiDigit(span[position])) { position++; }
                _ = IntervalNumber(span[numberStart..numberEnd], true, false);
                decimal months = IntervalNumber(span[monthStart..position], false, false);
                if (months >= 12 || position < span.Length && !IsAsciiWhiteSpace(span[position])) { throw InvalidInterval(); }
                RejectYearMonth("YEAR");
            }
            while (position < span.Length && IsAsciiWhiteSpace(span[position])) { position++; }
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
            // PostgreSQL DecodeInterval defaults an unqualified rightmost number to seconds
            // (src/backend/utils/adt/datetime.c:3625-3657), not TimeSpan.Parse's days.
            if (unit.Length == 0 && position == span.Length) { field = 3; }
            else if (unit.Length == 0 && position < span.Length)
            {
                int end = position;
                while (end < span.Length && !IsAsciiWhiteSpace(span[end])) { end++; }
                // DecodeInterval reads right to left: an hour unit sets the next
                // implicit field to DAY (PostgreSQL datetime.c:3748).
                if (span[position..end].Contains(':') || HasFollowingHour(span[position..])) { field = 0; }
            }
            if (field < 0 || (seen & (1 << field)) != 0 || unit.Length > 0 && position < span.Length && !IsAsciiWhiteSpace(span[position])) { throw InvalidInterval(); }
            ticks += number * FieldTicks(field);
            seen |= 1 << field; any = true;
        }
        if (!any) { throw InvalidInterval(); }
        return ago ? -ticks : ticks;
    }

    private bool HasFollowingHour(ReadOnlySpan<char> text)
    {
        int position = 0;
        if (!text.IsEmpty && text[0] is '+' or '-') { position++; }
        while (position < text.Length && (char.IsAsciiDigit(text[position]) || text[position] == '.')) { position++; }
        if (position == 0) { return false; }
        text = text[position..].TrimStart();
        int end = 0;
        while (end < text.Length && char.IsAsciiLetter(text[end])) { end++; }
        return text[..end].ToString().ToUpperInvariant() is "H" or "HOUR" or "HOURS" or "HR" or "HRS";
    }

    private decimal IntervalClock(ReadOnlySpan<char> text)
    {
        // DecodeTimeCommon accepts hh:mm, hh:mm:ss[.fraction], and mm:ss.fraction;
        // hours are unbounded, minutes are 0..59 and seconds 0..60 (datetime.c:2656-2740).
        int sign = 1;
        if (!text.IsEmpty && text[0] is '+' or '-') { sign = text[0] == '-' ? -1 : 1; text = text[1..]; }
        int colon = text.IndexOf(':');
        if (colon < 0) { throw InvalidInterval(); }
        decimal first = IntervalNumber(text[..colon], false, false);
        ReadOnlySpan<char> rest = text[(colon + 1)..];
        int secondColon = rest.IndexOf(':');
        decimal hours = first, minutes, seconds = 0;
        if (secondColon >= 0)
        {
            minutes = ClockField(rest[..secondColon], fraction: false);
            seconds = ClockField(rest[(secondColon + 1)..], fraction: true);
        }
        else if (rest.Contains('.'))
        {
            hours = 0; minutes = first;
            seconds = ClockField(rest, fraction: true);
        }
        else { minutes = ClockField(rest, fraction: false); }
        if (minutes >= 60 || seconds >= 61) { throw InvalidInterval(); }
        return sign * (hours * TimeSpan.TicksPerHour + minutes * TimeSpan.TicksPerMinute + seconds * TimeSpan.TicksPerSecond);
    }

    private decimal ClockField(ReadOnlySpan<char> text, bool fraction)
    {
        // DecodeTimeCommon's integer reader permits empty minute/second fields, and
        // ParseFraction accepts a bare dot (PostgreSQL datetime.c:691-721, 2672-2715).
        if (text.IsEmpty) { return 0; }
        int dot = text.IndexOf('.');
        if (!fraction || dot < 0) { return IntervalNumber(text, false, false); }
        if (dot > 0 && !Digits(text[..dot]) || text.Length - dot - 1 > 7
            || dot + 1 < text.Length && !Digits(text[(dot + 1)..])) { throw InvalidInterval(); }
        return text.Length == 1 ? 0 : decimal.Parse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }

    private decimal IntervalNumber(ReadOnlySpan<char> text, bool allowSign, bool fraction)
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

    private int Field(string field) => field switch { "DAY" => 0, "HOUR" => 1, "MINUTE" => 2, "SECOND" => 3, _ => -1 };
    private long FieldTicks(int field) => field switch
    {
        0 => TimeSpan.TicksPerDay, 1 => TimeSpan.TicksPerHour, 2 => TimeSpan.TicksPerMinute,
        4 => 7 * TimeSpan.TicksPerDay, 5 => TimeSpan.TicksPerMillisecond, 6 => TimeSpan.TicksPerMicrosecond,
        _ => TimeSpan.TicksPerSecond,
    };
    private void RejectYearMonth(string field)
    {
        if (field is "Y" or "YEAR" or "YEARS" or "YR" or "YRS" or "MONTH" or "MONTHS" or "MON" or "MONS"
            or "DECADE" or "DECADES" or "DEC" or "DECS" or "CENTURY" or "CENTURIES" or "CENT" or "C"
            or "MILLENNIUM" or "MILLENNIA" or "MIL" or "MILS" or "QUARTER" or "QTR") { throw Failure(SqlTemporalFailureKind.UnsupportedYearMonth, _message(SqlTemporalTextError.IntervalYearMonthUnsupported)); }
    }
    private bool Digits(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) { return false; }
        foreach (char character in text) { if (!char.IsAsciiDigit(character)) { return false; } }
        return true;
    }
    private int Number(ReadOnlySpan<char> text) => int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
    private bool IsAsciiWhiteSpace(char character) => character is ' ' or '\t' or '\n' or '\r' or '\v' or '\f';
    private SqlTemporalReadException Failure(SqlTemporalFailureKind kind, string message, Exception? innerException = null)
        => new(kind, message, innerException);

    private SqlTemporalReadException InvalidTime() => Failure(SqlTemporalFailureKind.Format, _message(SqlTemporalTextError.TimeTextInvalid));
    private SqlTemporalReadException InvalidInterval() => Failure(SqlTemporalFailureKind.Format, _message(SqlTemporalTextError.IntervalTextInvalid));
}
