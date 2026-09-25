using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Scheduler.Cron;

/// <summary>
/// Represents a parsed five-field cron expression: minute, hour, day of month, month,
/// and day of week.
/// </summary>
/// <remarks>
/// Fields accept wildcards, comma-separated lists, inclusive ranges, and positive
/// <c>/step</c> suffixes. Sunday is 0 or 7. When both day fields are restricted,
/// standard cron OR semantics apply; otherwise the restricted field governs.
/// </remarks>
public readonly struct Crontab : IEquatable<Crontab>, IEnumerable<DateTime>, IFormattable
{
    /// <summary>The range separator.</summary>
    public const char RangeValue = '-';

    /// <summary>The step separator.</summary>
    public const char StepValue = '/';

    /// <summary>The wildcard token.</summary>
    public const char Any = '*';

    /// <summary>The list separator.</summary>
    public const char ListSeparator = ',';

    private Crontab(
        CrontabField minute,
        CrontabField hour,
        CrontabField dayOfMonth,
        CrontabField month,
        CrontabField dayOfWeek)
    {
        Minute = minute;
        Hour = hour;
        DayOfMonth = dayOfMonth;
        Month = month;
        DayOfWeek = dayOfWeek;
    }

    /// <summary>Gets the minute field.</summary>
    public CrontabField Minute { get; }

    /// <summary>Gets the hour field.</summary>
    public CrontabField Hour { get; }

    /// <summary>Gets the day-of-month field.</summary>
    public CrontabField DayOfMonth { get; }

    /// <summary>Gets the month field.</summary>
    public CrontabField Month { get; }

    /// <summary>Gets the day-of-week field.</summary>
    public CrontabField DayOfWeek { get; }

    /// <summary>
    /// Gets the delay from local time until the next occurrence.
    /// </summary>
    /// <returns>The positive delay.</returns>
    public TimeSpan GetTimeSpan() => GetDateTime().Subtract(DateTime.Now);

    /// <summary>
    /// Gets the first occurrence strictly after local time.
    /// </summary>
    /// <returns>The next occurrence at minute precision.</returns>
    public DateTime GetDateTime() => GetDateTime(DateTime.Now);

    /// <summary>
    /// Gets the first occurrence strictly after a supplied instant.
    /// </summary>
    /// <param name="start">The exclusive lower bound.</param>
    /// <returns>The next occurrence at minute precision.</returns>
    /// <exception cref="InvalidOperationException">The expression cannot occur in a Gregorian calendar cycle.</exception>
    public DateTime GetDateTime(DateTime start)
    {
        DateTime candidate = new(
            start.Year,
            start.Month,
            start.Day,
            start.Hour,
            start.Minute,
            0,
            start.Kind);
        candidate = candidate.AddMinutes(1);

        int limitYear = Math.Min(DateTime.MaxValue.Year, start.Year + 400);
        DateTime limit = new(limitYear, 12, 31, 23, 59, 0, start.Kind);

        while (candidate <= limit)
        {
            if (!Month.Contains(candidate.Month))
            {
                candidate = FirstMinuteOfNextMonth(candidate);
                continue;
            }

            if (!MatchesDay(candidate))
            {
                candidate = candidate.Date.AddDays(1);
                continue;
            }

            if (!Hour.Contains(candidate.Hour))
            {
                candidate = candidate.AddHours(1);
                candidate = new DateTime(
                    candidate.Year,
                    candidate.Month,
                    candidate.Day,
                    candidate.Hour,
                    0,
                    0,
                    candidate.Kind);
                continue;
            }

            if (!Minute.Contains(candidate.Minute))
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            return candidate;
        }

        throw new InvalidOperationException(
            $"Cron expression '{this}' has no occurrence in a complete Gregorian calendar cycle.");
    }

    /// <summary>
    /// Determines whether a minute matches this expression.
    /// </summary>
    /// <param name="value">The minute to evaluate.</param>
    /// <returns><see langword="true"/> when the minute matches.</returns>
    public bool IsMatch(DateTime value)
    {
        return Month.Contains(value.Month) &&
            MatchesDay(value) &&
            Hour.Contains(value.Hour) &&
            Minute.Contains(value.Minute);
    }

    /// <summary>
    /// Parses an exact five-field cron expression.
    /// </summary>
    /// <param name="expression">The expression to parse.</param>
    /// <returns>The parsed expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">The expression does not contain exactly five valid fields.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A field value is outside its legal bounds.</exception>
    public static Crontab Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        string[] segments = expression.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length != 5)
        {
            throw new FormatException(
                $"Cron expression '{expression}' must contain exactly five fields.");
        }

        return new Crontab(
            CrontabField.ParseMinute(segments[0]),
            CrontabField.ParseHour(segments[1]),
            CrontabField.ParseDayOfMonth(segments[2]),
            CrontabField.ParseMonth(segments[3]),
            CrontabField.ParseDayOfWeek(segments[4]));
    }

    /// <summary>
    /// Attempts to parse an exact five-field cron expression.
    /// </summary>
    /// <param name="expression">The expression to parse.</param>
    /// <param name="crontab">The parsed expression on success.</param>
    /// <returns><see langword="true"/> when parsing succeeds.</returns>
    public static bool TryParse(
        [NotNullWhen(true)] string? expression,
        out Crontab crontab)
    {
        if (expression is null)
        {
            crontab = default;
            return false;
        }

        try
        {
            crontab = Parse(expression);
            return true;
        }
        catch (FormatException)
        {
            crontab = default;
            return false;
        }
        catch (ArgumentException)
        {
            crontab = default;
            return false;
        }
    }

    /// <inheritdoc />
    public bool Equals(Crontab other) =>
        Minute.Expression == other.Minute.Expression &&
        Hour.Expression == other.Hour.Expression &&
        DayOfMonth.Expression == other.DayOfMonth.Expression &&
        Month.Expression == other.Month.Expression &&
        DayOfWeek.Expression == other.DayOfWeek.Expression;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Crontab other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        Minute.Expression,
        Hour.Expression,
        DayOfMonth.Expression,
        Month.Expression,
        DayOfWeek.Expression);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) =>
        $"{Minute} {Hour} {DayOfMonth} {Month} {DayOfWeek}";

    /// <inheritdoc />
    public override string ToString() => ToString(null, null);

    /// <summary>Converts a string expression to a parsed crontab.</summary>
    /// <param name="expression">The five-field expression.</param>
    public static implicit operator Crontab(string expression) => Parse(expression);

    /// <summary>Compares two parsed expressions.</summary>
    public static bool operator ==(Crontab left, Crontab right) => left.Equals(right);

    /// <summary>Compares two parsed expressions.</summary>
    public static bool operator !=(Crontab left, Crontab right) => !left.Equals(right);

    /// <inheritdoc />
    public IEnumerator<DateTime> GetEnumerator() => new CrontabEnumerator(this);

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private bool MatchesDay(DateTime candidate)
    {
        bool dayOfMonthMatches = DayOfMonth.Contains(candidate.Day);
        bool dayOfWeekMatches = DayOfWeek.Contains((int)candidate.DayOfWeek);
        bool dayOfMonthRestricted = !DayOfMonth.IsWildcard;
        bool dayOfWeekRestricted = !DayOfWeek.IsWildcard;

        return dayOfMonthRestricted && dayOfWeekRestricted
            ? dayOfMonthMatches || dayOfWeekMatches
            : dayOfMonthMatches && dayOfWeekMatches;
    }

    private static DateTime FirstMinuteOfNextMonth(DateTime candidate)
    {
        DateTime first = new(
            candidate.Year,
            candidate.Month,
            1,
            0,
            0,
            0,
            candidate.Kind);
        return first.AddMonths(1);
    }

    private sealed class CrontabEnumerator : IEnumerator<DateTime>
    {
        private readonly Crontab _crontab;
        private DateTime? _current;

        /// <summary>Initializes a new instance of the <see cref="CrontabEnumerator"/> class.</summary>
        /// <param name="crontab">The cron expression whose occurrences are enumerated.</param>
        public CrontabEnumerator(Crontab crontab)
        {
            _crontab = crontab;
        }

        public DateTime Current => _current ?? throw new InvalidOperationException(
            "MoveNext must be called before reading the cron occurrence.");

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            _current = _crontab.GetDateTime(_current ?? DateTime.Now);
            return true;
        }

        public void Reset() => _current = null;

        public void Dispose()
        {
        }
    }
}
