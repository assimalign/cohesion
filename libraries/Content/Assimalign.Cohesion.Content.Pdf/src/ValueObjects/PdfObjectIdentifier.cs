using System;

namespace Assimalign.Cohesion.Content.Pdf;

/// <summary>The object number and generation identifying an indirect object (§7.3.10).</summary>
/// <remarks>Default denotes the reserved free-list head (0, 0), never an in-use object.</remarks>
public readonly record struct PdfObjectIdentifier
{
    private PdfObjectIdentifier(int number, int generation) { Number = number; Generation = generation; }
    /// <summary>Gets the nonnegative object number.</summary>
    public int Number { get; }
    /// <summary>Gets the generation (0–65535).</summary>
    public int Generation { get; }
    /// <summary>Creates an identifier.</summary>
    /// <param name="number">The nonnegative object number; zero is reserved for free entries.</param>
    /// <param name="generation">The generation (0–65535).</param>
    /// <returns>The identifier.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A component is outside its range.</exception>
    public static PdfObjectIdentifier Create(int number, int generation = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(number);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(generation, 65535);
        return new(number, generation);
    }
}
