using System;

namespace Assimalign.Cohesion.Content.Pdf;

/// <summary>A PDF signed integer (§7.3.3).</summary>
public sealed class PdfInteger : IPdfObject
{
    private PdfInteger(long value) => Value = value;
    /// <summary>Gets the value.</summary>
    public long Value { get; }
    /// <summary>Creates a PDF integer.</summary>
    /// <param name="value">The value to retain.</param>
    /// <returns>The new object.</returns>

    public static PdfInteger Create(long value)
    {
        return new PdfInteger(value);
    }
}
