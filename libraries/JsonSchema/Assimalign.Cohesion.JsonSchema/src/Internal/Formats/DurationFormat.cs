using System;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The <c>duration</c> format: the <c>duration</c> rule of RFC 3339 Appendix A.
/// </summary>
/// <remarks>
/// <code>
/// duration  = "P" (dur-date / dur-time / dur-week)
/// dur-date  = (dur-day / dur-month / dur-year) [dur-time]
/// dur-time  = "T" (dur-hour / dur-minute / dur-second)
/// dur-year  = 1*DIGIT "Y" [dur-month]   dur-month = 1*DIGIT "M" [dur-day]   dur-day = 1*DIGIT "D"
/// dur-hour  = 1*DIGIT "H" [dur-minute]  dur-minute = 1*DIGIT "M" [dur-second] dur-second = 1*DIGIT "S"
/// dur-week  = 1*DIGIT "W"
/// </code>
/// Each unit can only be followed by the next smaller one, so <c>P1Y2D</c> and <c>PT1H2S</c> are
/// invalid; weeks combine with nothing; fractions, signs and exponents are not part of the grammar.
/// </remarks>
internal static class DurationFormat
{
    private const string dateUnits = "YMD";
    private const string timeUnits = "HMS";

    public static bool IsDuration(ReadOnlySpan<char> value, JsonSchemaVersion version)
    {
        if (value.Length < 3 || value[0] != 'P')
        {
            return false;
        }

        int position = 1;
        if (value[position] != 'T')
        {
            if (!TryReadComponent(value, ref position, out char unit))
            {
                return false;
            }

            if (unit == 'W')
            {
                return position == value.Length;
            }

            int previous = dateUnits.IndexOf(unit);
            if (previous < 0)
            {
                return false;
            }

            while (position < value.Length && value[position] != 'T')
            {
                if (!TryReadComponent(value, ref position, out unit) || dateUnits.IndexOf(unit) != previous + 1)
                {
                    return false;
                }

                previous++;
            }

            if (position == value.Length)
            {
                return true;
            }
        }

        // dur-time: "T" then at least one of H, M, S, each followed only by the next.
        position++;
        int last = -1;
        while (position < value.Length)
        {
            if (!TryReadComponent(value, ref position, out char unit))
            {
                return false;
            }

            int index = timeUnits.IndexOf(unit);
            if (index < 0 || (last >= 0 && index != last + 1))
            {
                return false;
            }

            last = index;
        }

        return last >= 0;
    }

    /// <summary>Reads <c>1*DIGIT</c> and the unit letter after it.</summary>
    private static bool TryReadComponent(ReadOnlySpan<char> value, ref int position, out char unit)
    {
        int start = position;
        while (position < value.Length && char.IsAsciiDigit(value[position]))
        {
            position++;
        }

        if (position == start || position == value.Length)
        {
            unit = default;
            return false;
        }

        unit = value[position++];
        return true;
    }
}
