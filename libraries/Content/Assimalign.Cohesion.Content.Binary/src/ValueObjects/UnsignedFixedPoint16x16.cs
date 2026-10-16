using System;
using System.Globalization;

namespace Assimalign.Cohesion.Content.Binary;

/// <summary>A fixed-point value preserving its raw uint representation.</summary>
public readonly struct UnsignedFixedPoint16x16 : IEquatable<UnsignedFixedPoint16x16>
{
    private UnsignedFixedPoint16x16(uint rawValue) => RawValue = rawValue;

    /// <summary>Gets the encoded integer, including fractional bits.</summary>
    public uint RawValue { get; }

    /// <summary>Creates a value without changing its encoded bits.</summary>
    /// <param name="rawValue">The raw encoded integer.</param>
    /// <returns>The fixed-point value.</returns>
    public static UnsignedFixedPoint16x16 FromRaw(uint rawValue) => new UnsignedFixedPoint16x16(rawValue);

    /// <summary>Converts a finite number, rounding to the nearest raw integer with ties to even.</summary>
    /// <param name="value">The number to represent.</param>
    /// <returns>The rounded fixed-point value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside the representable range, or is not finite.</exception>
    public static UnsignedFixedPoint16x16 FromDouble(double value)
    {
        var raw = Math.Round(value * 65536.0, MidpointRounding.ToEven);
        if (!double.IsFinite(value) || value < uint.MinValue / 65536.0 || value > uint.MaxValue / 65536.0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), ErrorMessages.FixedPointOutOfRange);
        }
        return new UnsignedFixedPoint16x16((uint)raw);
    }

    /// <summary>Converts the raw value to a floating-point number exactly.</summary>
    /// <returns>The represented number.</returns>
    public double ToDouble() => RawValue / 65536.0;

    /// <inheritdoc/>
    public bool Equals(UnsignedFixedPoint16x16 other) => RawValue == other.RawValue;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is UnsignedFixedPoint16x16 other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => RawValue.GetHashCode();

    /// <summary>Formats the represented number using invariant culture.</summary>
    /// <returns>The invariant decimal representation.</returns>
    public override string ToString() => ToDouble().ToString(CultureInfo.InvariantCulture);
}
