using System;

namespace Assimalign.Cohesion.Content.Pdf;

/// <summary>A PDF finite real number (§7.3.3).</summary>
public sealed class PdfReal : IPdfObject
{
    private PdfReal(double value) => Value = value;
    /// <summary>Gets the value.</summary>
    public double Value { get; }
    /// <summary>Creates a PDF real.</summary>
    /// <param name="value">The value to retain.</param>
    /// <returns>The new object.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite.</exception>
    public static PdfReal Create(double value)
    {
        if (!double.IsFinite(value)) { throw new ArgumentOutOfRangeException(nameof(value), ErrorMessages.RealInvalid); }
        return new PdfReal(value);
    }
}
