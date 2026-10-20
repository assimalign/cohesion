using System;
using System.Globalization;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>UTC temporal fields; TIMESTAMP wall clocks read as UTC when meeting instants.</summary>
internal static class SqlTemporalFunctions
{
    internal static bool AcceptsField(string field, object value) => AcceptsField(field, value switch
    {
        DateOnly => Types.DatabaseType.Date, TimeOnly => Types.DatabaseType.Time,
        DateTime => Types.DatabaseType.DateTime, DateTimeOffset => Types.DatabaseType.DateTimeOffset,
        TimeSpan => Types.DatabaseType.TimeSpan, _ => Types.DatabaseType.Null,
    });

    internal static bool AcceptsField(string field, Types.DatabaseType type)
    {
        field = field.ToUpperInvariant();
        bool calendar = field is "YEAR" or "QUARTER" or "MONTH" or "WEEK" or "DAY" or "DOW" or "DOY" or "EPOCH";
        bool clock = field is "HOUR" or "MINUTE" or "SECOND" or "EPOCH";
        return type switch
        {
            Types.DatabaseType.Null or Types.DatabaseType.DateTime or Types.DatabaseType.DateTimeOffset => calendar || clock,
            Types.DatabaseType.Date => calendar,
            Types.DatabaseType.Time => clock,
            Types.DatabaseType.TimeSpan => clock || field == "DAY",
            _ => false,
        };
    }

    internal static bool AcceptsTruncation(string field, Types.DatabaseType type)
    {
        field = field.ToUpperInvariant();
        bool duration = field is "DAY" or "HOUR" or "MINUTE" or "SECOND" or "MILLISECOND" or "MICROSECOND";
        return duration || type is Types.DatabaseType.Null or Types.DatabaseType.Date or Types.DatabaseType.DateTime or Types.DatabaseType.DateTimeOffset &&
            field is "YEAR" or "QUARTER" or "MONTH" or "WEEK";
    }

    internal static decimal Extract(string field, object value)
    {
        field = field.ToUpperInvariant();
        if (!AcceptsField(field, value)) { throw SqlEvaluationException.FunctionArgumentTypeMismatch("EXTRACT", value.GetType().Name, "EXTRACT(field FROM compatible temporal value)"); }
        if (value is TimeOnly time) { return ExtractDuration(field, time.ToTimeSpan()); }
        if (value is TimeSpan interval) { return ExtractDuration(field, interval); }
        DateTime timestamp = value switch
        {
            DateOnly date => date.ToDateTime(TimeOnly.MinValue),
            DateTime wall => wall,
            DateTimeOffset instant => instant.UtcDateTime,
            _ => throw SqlEvaluationException.FunctionArgumentTypeMismatch("EXTRACT", value.GetType().Name, "EXTRACT(field FROM temporal value)"),
        };
        return field switch
        {
            "YEAR" => timestamp.Year, "QUARTER" => (timestamp.Month - 1) / 3 + 1, "MONTH" => timestamp.Month,
            "WEEK" => ISOWeek.GetWeekOfYear(timestamp), "DAY" => timestamp.Day, "DOW" => (int)timestamp.DayOfWeek,
            "DOY" => timestamp.DayOfYear, "HOUR" => timestamp.Hour, "MINUTE" => timestamp.Minute,
            "SECOND" => timestamp.Second + timestamp.Ticks % TimeSpan.TicksPerSecond / (decimal)TimeSpan.TicksPerSecond,
            "EPOCH" => (timestamp.Ticks - DateTime.UnixEpoch.Ticks) / (decimal)TimeSpan.TicksPerSecond,
            _ => throw SqlEvaluationException.FunctionArgumentTypeMismatch("EXTRACT", field, "EXTRACT(known field FROM temporal value)"),
        };
    }

    private static decimal ExtractDuration(string field, TimeSpan value) => field switch
    {
        "DAY" => value.Days, "HOUR" => value.Hours, "MINUTE" => value.Minutes,
        "SECOND" => value.Ticks % TimeSpan.TicksPerMinute / (decimal)TimeSpan.TicksPerSecond,
        "EPOCH" => value.Ticks / (decimal)TimeSpan.TicksPerSecond,
        _ => throw SqlEvaluationException.FunctionArgumentTypeMismatch("EXTRACT", field, "EXTRACT(day/hour/minute/second/epoch FROM INTERVAL)"),
    };

    internal static object Truncate(string field, object value)
    {
        field = field.ToUpperInvariant();
        if (value is TimeSpan duration) { return TruncateDuration(field, duration); }
        if (value is TimeOnly time) { return TimeOnly.FromTimeSpan(TruncateDuration(field, time.ToTimeSpan())); }
        DateTime timestamp = value switch
        {
            DateOnly date => date.ToDateTime(TimeOnly.MinValue), DateTime wall => wall,
            DateTimeOffset instant => instant.UtcDateTime,
            _ => throw SqlEvaluationException.FunctionArgumentTypeMismatch("DATE_TRUNC", value.GetType().Name, "DATE_TRUNC(field, temporal)"),
        };
        DateTime truncated = field switch
        {
            "YEAR" => new(timestamp.Year, 1, 1),
            "QUARTER" => new(timestamp.Year, (timestamp.Month - 1) / 3 * 3 + 1, 1),
            "MONTH" => new(timestamp.Year, timestamp.Month, 1),
            "WEEK" => timestamp.Date.AddDays(-((int)timestamp.DayOfWeek + 6) % 7),
            "DAY" => timestamp.Date,
            "HOUR" => new(timestamp.Ticks / TimeSpan.TicksPerHour * TimeSpan.TicksPerHour),
            "MINUTE" => new(timestamp.Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute),
            "SECOND" => new(timestamp.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond),
            "MILLISECOND" => new(timestamp.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond),
            "MICROSECOND" => new(timestamp.Ticks / 10 * 10),
            _ => throw SqlEvaluationException.FunctionArgumentTypeMismatch("DATE_TRUNC", field, "DATE_TRUNC(year/quarter/month/week/day/hour/minute/second/millisecond/microsecond, temporal)"),
        };
        return value switch
        {
            DateOnly => DateOnly.FromDateTime(truncated), DateTimeOffset => new DateTimeOffset(truncated, TimeSpan.Zero),
            _ => DateTime.SpecifyKind(truncated, DateTimeKind.Unspecified),
        };
    }

    private static TimeSpan TruncateDuration(string field, TimeSpan value)
    {
        long unit = field switch
        {
            "DAY" => TimeSpan.TicksPerDay, "HOUR" => TimeSpan.TicksPerHour,
            "MINUTE" => TimeSpan.TicksPerMinute, "SECOND" => TimeSpan.TicksPerSecond,
            "MILLISECOND" => TimeSpan.TicksPerMillisecond, "MICROSECOND" => 10,
            _ => throw SqlEvaluationException.FunctionArgumentTypeMismatch("DATE_TRUNC", field, "DATE_TRUNC(day/hour/minute/second/millisecond/microsecond, TIME/INTERVAL)"),
        };
        return new TimeSpan(value.Ticks / unit * unit);
    }
}
