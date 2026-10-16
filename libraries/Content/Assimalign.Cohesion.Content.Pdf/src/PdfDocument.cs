using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace Assimalign.Cohesion.Content.Pdf;

/// <summary>A random-access PDF model retaining identities, trailer and lazy source data (§7.5).</summary>
/// <remarks>Load through PdfReader. Sequential use only; keep the source open and unchanged.</remarks>
public sealed partial class PdfDocument : IDisposable
{
    private readonly Stream _source;
    private readonly bool _leaveOpen;
    private bool _disposed;
    private readonly Dictionary<int, PdfCrossReferenceEntry> _entries;
    internal PdfDocument(Stream source, long sourceOffset, Version version, PdfDictionary trailer,
        Dictionary<int, PdfCrossReferenceEntry> entries, long startXref, bool leaveOpen)
    {
        _source = source; SourceOffset = sourceOffset; Version = version; Trailer = trailer;
        _entries = entries; CrossReferences = new ReadOnlyDictionary<int, PdfCrossReferenceEntry>(entries);
        StartXref = startXref; _leaveOpen = leaveOpen;
    }
    /// <summary>Gets the effective version (header, upgraded by the catalog when present).</summary>
    public Version Version { get; internal set; }
    /// <summary>Gets the merged trailer, newest revision winning.</summary>
    public PdfDictionary Trailer { get; }
    /// <summary>Gets current cross-reference records by object number, including free entries.</summary>
    public IReadOnlyDictionary<int, PdfCrossReferenceEntry> CrossReferences { get; }
    /// <summary>Gets the latest xref offset relative to the PDF origin, or -1 after recovery.</summary>
    public long StartXref { get; }
    /// <summary>Gets the PDF origin in source coordinates.</summary>
    public long SourceOffset { get; }
    /// <summary>Gets whether an encryption dictionary was declared.</summary>
    public bool IsEncrypted => Trailer.Get("Encrypt") is not (null or PdfNull);
    /// <summary>Closes the document and, only when requested at load time, its source.</summary>
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        if (!_leaveOpen) { _source.Dispose(); }
    }
}
