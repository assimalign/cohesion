using System;

namespace Assimalign.Cohesion.Content.Pdf;

/// <summary>An unresolved reference with identifier-based equality (§7.3.10).</summary>
public sealed class PdfIndirectReference : IPdfObject, IEquatable<PdfIndirectReference>
{
    private PdfIndirectReference(PdfObjectIdentifier identifier) => Identifier = identifier;
    /// <summary>Gets the referenced identity.</summary>
    public PdfObjectIdentifier Identifier { get; }
    /// <summary>Creates an indirect reference.</summary>
    /// <param name="identifier">A nonzero object identity.</param>
    /// <returns>The reference.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The object number is zero.</exception>
    public static PdfIndirectReference Create(PdfObjectIdentifier identifier)
    {
        ArgumentOutOfRangeException.ThrowIfZero(identifier.Number);
        return new(identifier);
    }
    /// <inheritdoc/>
    public bool Equals(PdfIndirectReference? other) => other is not null && Identifier == other.Identifier;
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PdfIndirectReference other && Equals(other);
    /// <inheritdoc/>
    public override int GetHashCode() => Identifier.GetHashCode();
}
