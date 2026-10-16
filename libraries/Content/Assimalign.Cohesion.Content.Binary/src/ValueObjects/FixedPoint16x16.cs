using System;
using System.Globalization;

namespace Assimalign.Cohesion.Content.Binary;

/// <summary>A fixed-point value preserving its raw int representation.</summary>
public readonly struct FixedPoint16x16 : IEquatable<FixedPoint16x16>
{
    private FixedPoint16x16(int rawValue) => RawValue = rawValue;

    /// <summary>Gets the encoded integer, including fractional bits.</summary>
    public int RawValue { get; }

    /// <summary>Creates a value without changing its encoded bits.</summary>
    /// <param name="rawValue">The raw encoded integer.</param>
    /// <returns>The fixed-point value.</returns>
    public static FixedPoint16x16 FromRaw(int rawValue) => new FixedPoint16x16(rawValue);

    /// <summary>Converts a finite number, rounding to the nearest raw integer with ties to even.</summary>
    /// <param name="value">The number to represent.</param>
    /// <returns>The rounded fixed-point value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside the representable range, or is not finite.</exception>
    public static FixedPoint16x16 FromDouble(double value)
    {
        var raw = Math.Round(value * 65536.0, MidpointRounding.ToEven);
        if (!double.IsFinite(value) || value < int.MinValue / 65536.0 || value > int.MaxValue / 65536.0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), ErrorMessages.FixedPointOutOfRange);
        }
        return new FixedPoint16x16((int)raw);
    }

    /// <summary>Converts the raw value to a floating-point number exactly.</summary>
    /// <returns>The represented number.</returns>
    public double ToDouble() => RawValue / 65536.0;

    /// <inheritdoc/>
    public bool Equals(FixedPoint16x16 other) => RawValue == other.RawValue;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is FixedPoint16x16 other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => RawValue.GetHashCode();

    /// <summary>Formats the represented number using invariant culture.</summary>
    /// <returns>The invariant decimal representation.</returns>
    public override string ToString() => ToDouble().ToString(CultureInfo.InvariantCulture);
}
