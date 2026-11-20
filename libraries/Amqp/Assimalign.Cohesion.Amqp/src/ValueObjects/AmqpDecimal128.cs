using System;
using System.Globalization;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents an AMQP <c>decimal128</c> value: an IEEE 754-2008 decimal128 number in the binary integer
/// decimal encoding (AMQP 1.0 Part 1, section 1.6.14).
/// </summary>
/// <remarks>
/// .NET has no IEEE decimal type, so the value carries the encoding's bits unchanged and round-trips
/// exactly. Two values are equal when their bits are equal.
/// </remarks>
public readonly struct AmqpDecimal128 : IEquatable<AmqpDecimal128>
{
    /// <summary>
    /// Initializes a decimal128 value from its encoded bits.
    /// </summary>
    /// <param name="bits">The 128 bits of the binary integer decimal encoding.</param>
    public AmqpDecimal128(UInt128 bits)
    {
        Bits = bits;
    }

    /// <summary>
    /// Gets the 128 bits of the binary integer decimal encoding.
    /// </summary>
    public UInt128 Bits { get; }

    /// <summary>
    /// Determines whether two values have the same bits.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> when the values are equal; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(AmqpDecimal128 left, AmqpDecimal128 right) => left.Equals(right);

    /// <summary>
    /// Determines whether two values have different bits.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> when the values differ; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(AmqpDecimal128 left, AmqpDecimal128 right) => !left.Equals(right);

    /// <summary>
    /// Determines whether this value has the same bits as another.
    /// </summary>
    /// <param name="other">The value to compare with.</param>
    /// <returns><see langword="true"/> when the values are equal; otherwise <see langword="false"/>.</returns>
    public bool Equals(AmqpDecimal128 other) => Bits == other.Bits;

    /// <summary>
    /// Determines whether this value equals another object.
    /// </summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> is an equal value; otherwise <see langword="false"/>.</returns>
    public override bool Equals(object? obj) => obj is AmqpDecimal128 other && Equals(other);

    /// <summary>
    /// Returns a hash code over the bits.
    /// </summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => Bits.GetHashCode();

    /// <summary>
    /// Returns the bits as hexadecimal text.
    /// </summary>
    /// <returns>The text: <c>0x</c> followed by 32 hexadecimal digits.</returns>
    public override string ToString() => "0x" + Bits.ToString("x32", CultureInfo.InvariantCulture);
}
