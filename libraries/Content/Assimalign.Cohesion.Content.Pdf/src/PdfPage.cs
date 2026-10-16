namespace Assimalign.Cohesion.Content.Pdf;

/// <summary>A page dictionary and its resolved inherited attributes (§7.7.3).</summary>
/// <remarks>Obtain a fresh Pages view after editing the tree or attributes.</remarks>
public sealed class PdfPage
{
    internal PdfPage(PdfObjectIdentifier identifier, PdfDictionary dictionary, PdfDictionary resources, PdfArray mediaBox, PdfArray cropBox, int rotate)
    {
        Identifier = identifier; Dictionary = dictionary; Resources = resources; MediaBox = mediaBox; CropBox = cropBox; Rotate = rotate;
    }
    /// <summary>Gets the page's indirect identity.</summary>
    public PdfObjectIdentifier Identifier { get; }
    /// <summary>Gets the original editable page dictionary.</summary>
    public PdfDictionary Dictionary { get; }
    /// <summary>Gets the nearest inherited resource dictionary.</summary>
    public PdfDictionary Resources { get; }
    /// <summary>Gets the nearest inherited media rectangle.</summary>
    public PdfArray MediaBox { get; }
    /// <summary>Gets the inherited crop rectangle, defaulting to MediaBox.</summary>
    public PdfArray CropBox { get; }
    /// <summary>Gets the inherited rotation in degrees (a multiple of 90).</summary>
    public int Rotate { get; }
}
