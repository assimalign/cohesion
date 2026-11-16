using System;

namespace Assimalign.Cohesion.Database.Types.Text;

/// <summary>A one-based token position with a two-bit weight.</summary>
public readonly record struct TextPosition
{
    private TextPosition(int position, TextWeight weight)
    {
        Position = position;
        Weight = weight;
    }

    /// <summary>Gets the position, clamped to 16,383.</summary>
    public int Position { get; }

    /// <summary>Gets the weight.</summary>
    public TextWeight Weight { get; }

    /// <summary>Creates a position, clamping values above the PostgreSQL limit.</summary>
    /// <param name="position">A positive, one-based token position.</param>
    /// <param name="weight">The weight; the default is D.</param>
    /// <returns>The clamped position.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The position is not positive or the weight is invalid.</exception>
    public static TextPosition Create(int position, TextWeight weight = TextWeight.D)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((byte)weight, (byte)TextWeight.A);
        return new TextPosition(Math.Min(position, TextVector.MaxPosition), weight);
    }
}
