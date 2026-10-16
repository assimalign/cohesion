namespace Assimalign.Cohesion.Content.Pdf;

/// <summary>A retained cross-reference record (§7.5.4 and §7.5.8), including free-list metadata.</summary>
public readonly record struct PdfCrossReferenceEntry
{
    internal PdfCrossReferenceEntry(PdfObjectIdentifier identifier, PdfCrossReferenceKind kind, long offset, int objectStreamNumber, int objectStreamIndex, int nextFreeObjectNumber)
    {
        Identifier = identifier; Kind = kind; Offset = offset; ObjectStreamNumber = objectStreamNumber;
        ObjectStreamIndex = objectStreamIndex; NextFreeObjectNumber = nextFreeObjectNumber;
    }
    /// <summary>Gets the identity (compressed objects have generation zero).</summary>
    public PdfObjectIdentifier Identifier { get; }
    /// <summary>Gets the entry form.</summary>
    public PdfCrossReferenceKind Kind { get; }
    /// <summary>Gets the offset relative to the PDF origin for an uncompressed object.</summary>
    public long Offset { get; }
    /// <summary>Gets the containing object stream number for a compressed object.</summary>
    public int ObjectStreamNumber { get; }
    /// <summary>Gets the zero-based index within the object stream.</summary>
    public int ObjectStreamIndex { get; }
    /// <summary>Gets the next free object number.</summary>
    public int NextFreeObjectNumber { get; }
}

/// <summary>The cross-reference entry forms (§7.5.8.3).</summary>
public enum PdfCrossReferenceKind
{
    /// <summary>A free object, including its next-free link.</summary>
    Free,
    /// <summary>An object at a source offset.</summary>
    Uncompressed,
    /// <summary>An object in an object stream.</summary>
    Compressed
}
