using System;

namespace Assimalign.Cohesion.Content.Media;

/// <summary>An exact signed time in seconds, represented by an integer value divided by a timescale.</summary>
/// <remarks>Default is zero at timescale one. Equality compares time, not its representation.
/// A frame at 30000/1001 frames per second advances by 1001 at timescale 30000.
/// Arithmetic reduces results before checking the Int64 value and positive Int64 timescale limits.</remarks>
public readonly struct MediaTimestamp : IEquatable<MediaTimestamp>, IComparable<MediaTimestamp>
{
    private readonly long _timescale;

    /// <summary>Creates an exact time, retaining the supplied representation.</summary>
    /// <param name="value">The signed number of ticks.</param>
    /// <param name="timescale">The positive number of ticks per second.</param>
    /// <exception cref="ArgumentOutOfRangeException">The timescale is not positive.</exception>
    public MediaTimestamp(long value, long timescale)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timescale);
        Value = value;
        _timescale = timescale;
    }

    /// <summary>Gets the signed number of ticks.</summary>
    public long Value { get; }

    /// <summary>Gets the positive number of ticks per second.</summary>
    public long Timescale => _timescale == 0 ? 1 : _timescale;

    /// <summary>Converts to another timescale without rounding.</summary>
    /// <param name="timescale">The positive destination ticks per second.</param>
    /// <returns>The same time at the requested timescale.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The timescale is not positive.</exception>
    /// <exception cref="InvalidOperationException">The time is not integral at the requested timescale.</exception>
    /// <exception cref="OverflowException">The converted value does not fit in Int64.</exception>
    public MediaTimestamp RescaleExact(long timescale)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timescale);
        var numerator = (Int128)Value * timescale;
        if (numerator % Timescale != 0)
        {
            throw new InvalidOperationException(ErrorMessages.InexactTimestamp);
        }
        return FromWide(numerator / Timescale, timescale);
    }

    /// <summary>Converts using integer arithmetic and an explicit rounding policy.</summary>
    /// <param name="timescale">The positive destination ticks per second.</param>
    /// <param name="rounding">Nearest with even ties by default; all five MidpointRounding modes are supported.</param>
    /// <returns>The rounded time at the requested timescale.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The timescale or rounding mode is invalid.</exception>
    /// <exception cref="OverflowException">The rounded value does not fit in Int64.</exception>
    public MediaTimestamp Rescale(long timescale, MidpointRounding rounding = MidpointRounding.ToEven)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timescale);
        if (rounding < MidpointRounding.ToEven || rounding > MidpointRounding.ToPositiveInfinity)
        {
            throw new ArgumentOutOfRangeException(nameof(rounding), ErrorMessages.InvalidRounding);
        }

        var numerator = (Int128)Value * timescale;
        var quotient = numerator / Timescale;
        var remainder = numerator % Timescale;
        if (remainder != 0)
        {
            var direction = remainder < 0 ? -1 : 1;
            var twiceRemainder = Int128.Abs(remainder) * 2;
            var increment = rounding switch
            {
                MidpointRounding.ToZero => false,
                MidpointRounding.ToNegativeInfinity => direction < 0,
                MidpointRounding.ToPositiveInfinity => direction > 0,
                MidpointRounding.AwayFromZero => twiceRemainder >= Timescale,
                _ => twiceRemainder > Timescale || (twiceRemainder == Timescale && quotient % 2 != 0)
            };
            if (increment)
            {
                quotient += direction;
            }
        }
        return FromWide(quotient, timescale);
    }

    /// <summary>Converts to TimeSpan for display, with the same rounding policy as Rescale.</summary>
    /// <param name="rounding">The rounding policy for 100-nanosecond ticks.</param>
    /// <returns>The display time; retain this value object for timeline calculations.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The rounding mode is invalid.</exception>
    /// <exception cref="OverflowException">The result is outside TimeSpan's range.</exception>
    public TimeSpan ToTimeSpan(MidpointRounding rounding = MidpointRounding.ToEven) =>
        TimeSpan.FromTicks(Rescale(TimeSpan.TicksPerSecond, rounding).Value);

    /// <summary>Compares exact times using wide integer cross-products.</summary>
    /// <param name="other">The time to compare.</param>
    /// <returns>A negative value, zero, or a positive value for earlier, equal, or later.</returns>
    public int CompareTo(MediaTimestamp other) =>
        ((Int128)Value * other.Timescale).CompareTo((Int128)other.Value * Timescale);

    /// <inheritdoc/>
    public bool Equals(MediaTimestamp other) => CompareTo(other) == 0;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is MediaTimestamp other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var divisor = GreatestCommonDivisor(Int128.Abs(Value), Timescale);
        return HashCode.Combine((long)(Value / divisor), (long)(Timescale / divisor));
    }

    /// <summary>Adds exact times, reducing the result to lowest terms.</summary>
    /// <param name="left">The first time.</param>
    /// <param name="right">The time to add.</param>
    /// <returns>The exact sum.</returns>
    /// <exception cref="OverflowException">The reduced value or timescale cannot be represented.</exception>
    public static MediaTimestamp operator +(MediaTimestamp left, MediaTimestamp right) =>
        Combine(left, right, subtract: false);

    /// <summary>Subtracts exact times, reducing the result to lowest terms.</summary>
    /// <param name="left">The first time.</param>
    /// <param name="right">The time to subtract.</param>
    /// <returns>The exact difference.</returns>
    /// <exception cref="OverflowException">The reduced value or timescale cannot be represented.</exception>
    public static MediaTimestamp operator -(MediaTimestamp left, MediaTimestamp right) =>
        Combine(left, right, subtract: true);

    /// <summary>Tests exact time equality.</summary>
    /// <param name="left">The first time.</param><param name="right">The second time.</param>
    /// <returns>Whether the times are equal.</returns>
    public static bool operator ==(MediaTimestamp left, MediaTimestamp right) => left.Equals(right);

    /// <summary>Tests exact time inequality.</summary>
    /// <param name="left">The first time.</param><param name="right">The second time.</param>
    /// <returns>Whether the times differ.</returns>
    public static bool operator !=(MediaTimestamp left, MediaTimestamp right) => !left.Equals(right);

    /// <summary>Tests whether the first time is earlier.</summary>
    /// <param name="left">The first time.</param><param name="right">The second time.</param>
    /// <returns>Whether the first time is earlier.</returns>
    public static bool operator <(MediaTimestamp left, MediaTimestamp right) => left.CompareTo(right) < 0;

    /// <summary>Tests whether the first time is no later.</summary>
    /// <param name="left">The first time.</param><param name="right">The second time.</param>
    /// <returns>Whether the first time is earlier or equal.</returns>
    public static bool operator <=(MediaTimestamp left, MediaTimestamp right) => left.CompareTo(right) <= 0;

    /// <summary>Tests whether the first time is later.</summary>
    /// <param name="left">The first time.</param><param name="right">The second time.</param>
    /// <returns>Whether the first time is later.</returns>
    public static bool operator >(MediaTimestamp left, MediaTimestamp right) => left.CompareTo(right) > 0;

    /// <summary>Tests whether the first time is no earlier.</summary>
    /// <param name="left">The first time.</param><param name="right">The second time.</param>
    /// <returns>Whether the first time is later or equal.</returns>
    public static bool operator >=(MediaTimestamp left, MediaTimestamp right) => left.CompareTo(right) >= 0;

    private static MediaTimestamp Combine(MediaTimestamp left, MediaTimestamp right, bool subtract)
    {
        var divisor = GreatestCommonDivisor(left.Timescale, right.Timescale);
        var leftFactor = right.Timescale / divisor;
        var rightFactor = left.Timescale / divisor;
        var numerator = (Int128)left.Value * leftFactor;
        var rightNumerator = (Int128)right.Value * rightFactor;
        numerator = subtract ? numerator - rightNumerator : numerator + rightNumerator;
        var denominator = (Int128)left.Timescale * leftFactor;
        divisor = GreatestCommonDivisor(Int128.Abs(numerator), denominator);
        return FromWide(numerator / divisor, denominator / divisor);
    }

    private static MediaTimestamp FromWide(Int128 value, Int128 timescale)
    {
        if (value < long.MinValue || value > long.MaxValue || timescale > long.MaxValue)
        {
            throw new OverflowException(ErrorMessages.TimestampOverflow);
        }
        return new MediaTimestamp((long)value, (long)timescale);
    }

    private static Int128 GreatestCommonDivisor(Int128 left, Int128 right)
    {
        while (right != 0)
        {
            var remainder = left % right;
            left = right;
            right = remainder;
        }
        return left;
    }
}
