using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Assimalign.Cohesion.Content.Pdf.Internal;

internal sealed partial class PdfSaveSession : IDisposable
{
    private readonly PdfDocument _document;
    private readonly PdfWriterOptions _options;
    private readonly CancellationToken _cancellationToken;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly PdfOutput _output;
    private readonly PdfObjectSerializer _serializer;
    private readonly SortedDictionary<int, PdfCrossReferenceEntry> _entries = new();
    private readonly SortedDictionary<int, IPdfObject> _objects = new();
    private readonly Dictionary<int, long> _lengths = new();
    private readonly HashSet<int> _unpacked = new();
    private long _next;

    internal PdfSaveSession(PdfDocument document, Stream destination, PdfWriterOptions options, CancellationToken cancellationToken)
    {
        _document = document; _options = options; _cancellationToken = cancellationToken;
        _output = new PdfOutput(destination, cancellationToken, hash: _hash);
        _serializer = Serializer(_output);
    }
    private PdfObjectSerializer Serializer(PdfOutput output, bool copyPayloads = true) =>
        new(output, _options.MaxDepth, _options.MaxObjects, _options.CompressModifiedStreams, copyPayloads, _cancellationToken);

    internal void WriteFull()
    {
        if (_options.UseCrossReferenceStream && _document.Version < new Version(1, 5))
        {
            throw new ContentFormatException(ErrorMessages.VersionUnsupported, ContentFormatErrorCode.Unsupported, _document.SourceOffset);
        }
        PrepareFull();
        _output.Text(FormattableString.Invariant($"%PDF-{_document.Version.Major}.{_document.Version.Minor}\n"));
        _output.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A });
        WriteObjects(_options.PackObjects);
        LinkFreeEntries();
        var trailer = Trailer();
        long start;
        if (_options.UseCrossReferenceStream) { start = WriteCrossReferenceStream(trailer); }
        else { start = WriteTable(trailer); }
        Finish(start);
    }
    private void PrepareFull()
    {
        _ = _document.Catalog;
        // Recovery may replace the xref map during lazy resolution. Restart from its repaired map once.
        var previousStart = _document.StartXref;
        do
        {
            _objects.Clear(); _entries.Clear(); _lengths.Clear();
            _next = _document.NextObjectNumber;
            if (_next > _options.MaxObjects) { Limit(); }
            var sourceEntries = new List<PdfCrossReferenceEntry>(_document.CrossReferences.Values);
            foreach (var entry in sourceEntries)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (entry.Kind != PdfCrossReferenceKind.Free)
                {
                    var value = _document.Resolve(entry.Identifier);
                    _objects.Add(entry.Identifier.Number, value);
                }
                _entries[entry.Identifier.Number] = entry;
            }
            if (previousStart >= 0 && _document.StartXref < 0) { previousStart = -1; continue; }
            break;
        } while (true);
        for (var number = 0; number < _next; number++)
        {
            if (!_entries.ContainsKey(number))
            {
                _entries.Add(number, new(PdfObjectIdentifier.Create(number), PdfCrossReferenceKind.Free, 0, 0, 0, 0));
            }
        }
        ValidateObjects();
    }
    private void ValidateObjects()
    {
        if (_document.Trailer.Get("Root") is not PdfIndirectReference root ||
            !_document.CrossReferences.TryGetValue(root.Identifier.Number, out var catalogEntry) ||
            catalogEntry.Kind == PdfCrossReferenceKind.Free || catalogEntry.Identifier != root.Identifier)
        {
            throw new ContentFormatException(ErrorMessages.StructureInvalid, ContentFormatErrorCode.InvalidStructure, _document.SourceOffset);
        }
        _unpacked.Add(root.Identifier.Number);
        // Stream Length objects may not be compressed (§7.5.7), including lengths of preserved streams.
        foreach (var pair in _objects)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (pair.Value is PdfStream stream && stream.Dictionary.Get("Length") is PdfIndirectReference length)
            {
                _unpacked.Add(length.Identifier.Number);
            }
            using var counter = new PdfOutput(Stream.Null, _cancellationToken);
            Serializer(counter, copyPayloads: false).Write(pair.Value);
            _lengths[pair.Key] = counter.Count;
        }
        using var trailerCounter = new PdfOutput(Stream.Null, _cancellationToken);
        Serializer(trailerCounter, copyPayloads: false).Write(_document.Trailer);
    }
    private PdfObjectIdentifier Allocate()
    {
        if (_next > int.MaxValue || _entries.Count >= _options.MaxObjects) { Limit(); }
        var identifier = PdfObjectIdentifier.Create((int)_next++);
        _entries.Add(identifier.Number, new(identifier, PdfCrossReferenceKind.Uncompressed, 0, 0, 0, 0));
        return identifier;
    }
    private void WriteIndirect(PdfObjectIdentifier identifier, IPdfObject value)
    {
        _entries[identifier.Number] = new(identifier, PdfCrossReferenceKind.Uncompressed, _output.Count, 0, 0, 0);
        _output.Text(FormattableString.Invariant($"{identifier.Number} {identifier.Generation} obj\n"));
        _serializer.Write(value); _output.Text("\nendobj\n");
    }
    private void LinkFreeEntries()
    {
        var free = new List<int> { 0 };
        foreach (var pair in _entries) { if (pair.Key != 0 && pair.Value.Kind == PdfCrossReferenceKind.Free) { free.Add(pair.Key); } }
        for (var i = 0; i < free.Count; i++)
        {
            var number = free[i];
            var id = number == 0 ? PdfObjectIdentifier.Create(0, 65535) : _entries[number].Identifier;
            _entries[number] = new(id, PdfCrossReferenceKind.Free, 0, 0, 0, i + 1 < free.Count ? free[i + 1] : 0);
        }
    }
    private PdfDictionary Trailer()
    {
        var trailer = PdfObjectSerializer.CopyDictionary(_document.Trailer);
        foreach (var key in new[] { "Type", "W", "Index", "Length", "Filter", "DecodeParms", "Prev", "XRefStm", "F", "FFilter", "FDecodeParms", "DL" })
        {
            trailer.Remove(PdfName.Create(Encoding.ASCII.GetBytes(key)));
        }
        trailer[PdfName.Create("Size"u8)] = PdfInteger.Create(_next);
        if (trailer.Get("ID") is null or PdfNull)
        {
            // Stable content-derived identifiers, without rereading payloads or consulting clocks/randomness.
            var bytes = _hash.GetCurrentHash();
            var ids = new PdfArray();
            ids.Add(PdfString.Create(bytes.AsSpan(0, 16), true)); ids.Add(PdfString.Create(bytes.AsSpan(0, 16), true));
            trailer[PdfName.Create("ID"u8)] = ids;
        }
        return trailer;
    }
    private void Finish(long start) => _output.Text(FormattableString.Invariant($"startxref\n{start}\n%%EOF\n"));
    private void Limit() => throw new ContentFormatException(ErrorMessages.WriterLimit, ContentFormatErrorCode.LimitExceeded, _document.SourceOffset);
    public void Dispose() { _hash.Dispose(); _output.Dispose(); }
}
