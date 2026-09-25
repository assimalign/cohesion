using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Scheduler.Cron;

/// <summary>
/// Represents one parsed field of a five-field cron expression.
/// </summary>
public readonly struct CrontabField
{
    private readonly int[] _occurrences;

    private CrontabField(
        CrontabFieldKind kind,
        string expression,
        int minBoundary,
        int maxBoundary,
        bool isWildcard,
        int[] occurrences)
    {
        Kind = kind;
        Expression = expression;
        MinBoundary = minBoundary;
        MaxBoundary = maxBoundary;
        IsWildcard = isWildcard;
        _occurrences = occurrences;
    }

    /// <summary>
    /// Gets whether the field begins with the unrestricted wildcard token.
    /// </summary>
    public bool IsWildcard { get; }

    /// <summary>
    /// Gets whether every legal value is selected.
    /// </summary>
    public bool IsAll => _occurrences is not null && _occurrences.Length == MaxBoundary - MinBoundary + 1;

    /// <summary>
    /// Gets the sorted distinct legal values selected by the expression.
    /// </summary>
    public IReadOnlyList<int> Occurrences => _occurrences is null
        ? Array.Empty<int>()
        : Array.AsReadOnly(_occurrences);

    /// <summary>
    /// Gets the field kind.
    /// </summary>
    public CrontabFieldKind Kind { get; }

    /// <summary>
    /// Gets the source expression for this field.
    /// </summary>
    public string Expression { get; }

    /// <summary>
    /// Gets the largest legal normalized value.
    /// </summary>
    public int MaxBoundary { get; }

    /// <summary>
    /// Gets the smallest legal normalized value.
    /// </summary>
    public int MinBoundary { get; }

    /// <summary>Parses a minute field.</summary>
    /// <param name="expression">The field expression.</param>
    /// <returns>The parsed field.</returns>
    public static CrontabField ParseMinute(string expression) =>
        Parse(CrontabFieldKind.Minute, expression, 0, 59);

    /// <summary>Parses an hour field.</summary>
    /// <param name="expression">The field expression.</param>
    /// <returns>The parsed field.</returns>
    public static CrontabField ParseHour(string expression) =>
        Parse(CrontabFieldKind.Hour, expression, 0, 23);

    /// <summary>Parses a day-of-month field.</summary>
    /// <param name="expression">The field expression.</param>
    /// <returns>The parsed field.</returns>
    public static CrontabField ParseDayOfMonth(string expression) =>
        Parse(CrontabFieldKind.DayOfMonth, expression, 1, 31);

    /// <summary>Parses a month field.</summary>
    /// <param name="expression">The field expression.</param>
    /// <returns>The parsed field.</returns>
    public static CrontabField ParseMonth(string expression) =>
        Parse(CrontabFieldKind.Month, expression, 1, 12);

    /// <summary>
    /// Parses a day-of-week field. Both 0 and 7 denote Sunday.
    /// </summary>
    /// <param name="expression">The field expression.</param>
    /// <returns>The parsed field.</returns>
    public static CrontabField ParseDayOfWeek(string expression) =>
        Parse(CrontabFieldKind.DayOfWeek, expression, 0, 7, normalizeSunday: true);

    /// <summary>
    /// Determines whether the normalized field contains a value.
    /// </summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when selected.</returns>
    public bool Contains(int value) => Array.BinarySearch(_occurrences ?? [], value) >= 0;

    /// <inheritdoc />
    public override string ToString() => Expression ?? string.Empty;

    private static CrontabField Parse(
        CrontabFieldKind kind,
        string expression,
        int min,
        int max,
        bool normalizeSunday = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        if (expression.Any(char.IsWhiteSpace))
        {
            throw new FormatException($"Cron field '{expression}' cannot contain whitespace.");
        }

        var values = new SortedSet<int>();
        string[] items = expression.Split(',', StringSplitOptions.None);
        for (int index = 0; index < items.Length; index++)
        {
            ParseItem(items[index], expression, min, max, values);
        }

        int[] normalized = normalizeSunday
            ? values.Select(static value => value == 7 ? 0 : value).Distinct().Order().ToArray()
            : values.ToArray();

        return new CrontabField(
            kind,
            expression,
            min,
            normalizeSunday ? 6 : max,
            expression[0] == '*',
            normalized);
    }

    private static void ParseItem(
        string item,
        string expression,
        int min,
        int max,
        SortedSet<int> values)
    {
        if (item.Length == 0)
        {
            throw new FormatException($"Cron field '{expression}' contains an empty list item.");
        }

        string[] stepParts = item.Split('/', StringSplitOptions.None);
        if (stepParts.Length > 2 || stepParts[0].Length == 0)
        {
            throw new FormatException($"Cron field '{expression}' has an invalid step expression.");
        }

        int step = 1;
        if (stepParts.Length == 2)
        {
            if (!int.TryParse(stepParts[1], out step) || step <= 0)
            {
                throw new FormatException($"Cron field '{expression}' has a non-positive or invalid step.");
            }
        }

        string range = stepParts[0];
        int lower;
        int upper;
        if (range == "*")
        {
            lower = min;
            upper = max;
        }
        else
        {
            string[] bounds = range.Split('-', StringSplitOptions.None);
            if (bounds.Length == 1)
            {
                lower = ParseBound(bounds[0], expression, min, max);
                upper = stepParts.Length == 2 ? max : lower;
            }
            else if (bounds.Length == 2 && bounds[0].Length > 0 && bounds[1].Length > 0)
            {
                lower = ParseBound(bounds[0], expression, min, max);
                upper = ParseBound(bounds[1], expression, min, max);
                if (lower > upper)
                {
                    throw new FormatException(
                        $"Cron field '{expression}' has a descending range '{range}'.");
                }
            }
            else
            {
                throw new FormatException($"Cron field '{expression}' has an invalid range.");
            }
        }

        int value = lower;
        while (true)
        {
            values.Add(value);

            // Comparing the remaining bounded distance before addition prevents a legal,
            // very large step from wrapping Int32 and introducing out-of-range values.
            if (step > upper - value)
            {
                break;
            }

            value += step;
        }
    }

    private static int ParseBound(string text, string expression, int min, int max)
    {
        if (!int.TryParse(text, out int value))
        {
            throw new FormatException(
                $"Cron field '{expression}' contains non-numeric value '{text}'.");
        }

        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expression),
                value,
                $"Cron field values must be between {min} and {max}.");
        }

        return value;
    }
}
