using System;

namespace Assimalign.Cohesion.Content.Pdf;

/// <summary>A PDF boolean (§7.3.2).</summary>
public sealed class PdfBoolean : IPdfObject
{
    private PdfBoolean(bool value) => Value = value;
    /// <summary>Gets the value.</summary>
    public bool Value { get; }
    /// <summary>Creates a PDF boolean.</summary>
    /// <param name="value">The value to retain.</param>
    /// <returns>The new object.</returns>

    public static PdfBoolean Create(bool value)
    {
        return new PdfBoolean(value);
    }
}
