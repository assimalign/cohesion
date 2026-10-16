using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Pdf.Internal;

namespace Assimalign.Cohesion.Content.Pdf;

public sealed partial class PdfDocument
{
    /// <summary>Walks the current page tree, resolving inherited attributes into a fresh ordered view (§7.7.3).</summary>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    /// <exception cref="ContentFormatException">The tree is malformed, cyclic, over a limit or requires unsupported encryption.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    public IReadOnlyList<PdfPage> Pages
    {
        get
        {
            var root = Catalog.Get("Pages");
            if (root is not PdfIndirectReference reference) { throw new ContentFormatException(ErrorMessages.PageTreeInvalid, ContentFormatErrorCode.InvalidStructure, AbsoluteOrigin); }
            var pages = new List<PdfPage>();
            var visited = new HashSet<PdfObjectIdentifier>();
            WalkPages(reference, null, null, null, null, 0, 0, pages, visited);
            return pages.AsReadOnly();
        }
    }
    /// <summary>Gets the actual leaf count by walking the current page tree.</summary>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    /// <exception cref="ContentFormatException">The page tree cannot be resolved within the configured limits.</exception>
    /// <exception cref="System.IO.IOException">An underlying stream operation fails.</exception>
    /// <exception cref="System.NotSupportedException">An underlying stream rejects a required read, write or seek operation.</exception>
    public int PageCount => Pages.Count;
    private int WalkPages(PdfIndirectReference reference, PdfObjectIdentifier? parent, PdfDictionary? resources,
        PdfArray? mediaBox, PdfArray? cropBox, int rotate, int depth, List<PdfPage> pages, HashSet<PdfObjectIdentifier> visited)
    {
        if (depth >= _options.MaxDepth || visited.Count >= _options.MaxObjects) { throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, AbsoluteOrigin); }
        if (!visited.Add(reference.Identifier) || Resolve(reference.Identifier) is not PdfDictionary dictionary)
        {
            throw new ContentFormatException(ErrorMessages.PageTreeInvalid, ContentFormatErrorCode.InvalidStructure, AbsoluteOrigin);
        }
        var position = AbsoluteOrigin + (_entries.TryGetValue(reference.Identifier.Number, out var entry) ? entry.Offset : 0);
        if (parent.HasValue && (dictionary.Get("Parent") is not PdfIndirectReference parentReference || parentReference.Identifier != parent))
        {
            throw new ContentFormatException(ErrorMessages.PageTreeInvalid, ContentFormatErrorCode.InvalidStructure, position);
        }
        IPdfObject? Attribute(string name)
        {
            var value = dictionary.Get(name);
            return value is null ? null : ResolveValue(value);
        }
        if (Attribute("Resources") is IPdfObject resourceValue and not PdfNull)
        {
            resources = resourceValue as PdfDictionary ?? throw new ContentFormatException(ErrorMessages.PageTreeInvalid, ContentFormatErrorCode.InvalidStructure, position);
        }
        if (Attribute("MediaBox") is IPdfObject mediaValue and not PdfNull) { mediaBox = Rectangle(mediaValue, position); }
        if (Attribute("CropBox") is IPdfObject cropValue and not PdfNull) { cropBox = Rectangle(cropValue, position); }
        if (Attribute("Rotate") is IPdfObject rotation and not PdfNull)
        {
            if (rotation is not PdfInteger integer || integer.Value < int.MinValue || integer.Value > int.MaxValue || integer.Value % 90 != 0)
            {
                throw new ContentFormatException(ErrorMessages.PageTreeInvalid, ContentFormatErrorCode.InvalidValue, position);
            }
            rotate = (int)integer.Value;
        }
        if (PdfFileLoader.IsName(dictionary.Get("Type"), "Page"))
        {
            if (!parent.HasValue || mediaBox is null || resources is null) { throw new ContentFormatException(ErrorMessages.PageTreeInvalid, ContentFormatErrorCode.InvalidStructure, position); }
            pages.Add(new(reference.Identifier, dictionary, resources, mediaBox, cropBox ?? mediaBox, rotate));
            return 1;
        }
        if (!PdfFileLoader.IsName(dictionary.Get("Type"), "Pages") || Attribute("Kids") is not PdfArray kids)
        {
            throw new ContentFormatException(ErrorMessages.PageTreeInvalid, ContentFormatErrorCode.InvalidStructure, position);
        }
        var count = 0;
        foreach (var item in kids)
        {
            if (item is not PdfIndirectReference child) { throw new ContentFormatException(ErrorMessages.PageTreeInvalid, ContentFormatErrorCode.InvalidStructure, position); }
            count += WalkPages(child, reference.Identifier, resources, mediaBox, cropBox, rotate, depth + 1, pages, visited);
        }
        var declared = Attribute("Count");
        if (declared is not PdfInteger declaredCount || declaredCount.Value != count)
        {
            throw new ContentFormatException(ErrorMessages.PageTreeInvalid, ContentFormatErrorCode.InvalidStructure, position);
        }
        return count;
    }
    private PdfArray Rectangle(IPdfObject value, long position)
    {
        if (value is not PdfArray array || array.Count != 4) { throw new ContentFormatException(ErrorMessages.PageTreeInvalid, ContentFormatErrorCode.InvalidStructure, position); }
        var resolved = new PdfArray();
        foreach (var item in array)
        {
            var coordinate = ResolveValue(item);
            if (coordinate is not (PdfInteger or PdfReal)) { throw new ContentFormatException(ErrorMessages.PageTreeInvalid, ContentFormatErrorCode.InvalidStructure, position); }
            resolved.Add(coordinate);
        }
        return resolved;
    }
}
