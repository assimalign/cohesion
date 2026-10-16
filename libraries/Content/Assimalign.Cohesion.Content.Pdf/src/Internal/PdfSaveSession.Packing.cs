using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Pdf.Internal;

internal sealed partial class PdfSaveSession
{
    private void WriteObjects(bool pack)
    {
        var batch = new List<int>();
        long batchLength = 0;
        foreach (var pair in _objects)
        {
            var identifier = _entries[pair.Key].Identifier;
            var length = _lengths[pair.Key];
            // Indirect reference values are disallowed in object streams, as are streams and nonzero generations.
            if (!pack || identifier.Generation != 0 || _unpacked.Contains(pair.Key) || pair.Value is PdfStream or PdfIndirectReference ||
                length + 32 > _options.MaxBufferedBytes)
            {
                WriteIndirect(identifier, pair.Value); continue;
            }
            if (batch.Count == 100 || batchLength + length + 32 > _options.MaxBufferedBytes)
            {
                WriteObjectStream(batch); batch.Clear(); batchLength = 0;
            }
            batch.Add(pair.Key); batchLength += length + 32;
        }
        if (batch.Count != 0) { WriteObjectStream(batch); }
    }
    private void WriteObjectStream(List<int> numbers)
    {
        var identifier = Allocate();
        using var raw = new MemoryStream();
        using var bounded = new PdfOutput(raw, _cancellationToken, _options.MaxBufferedBytes);
        long offset = 0;
        foreach (var number in numbers)
        {
            bounded.Text(FormattableString.Invariant($"{number} {offset} "));
            offset += _lengths[number] + 1;
        }
        var first = bounded.Count;
        var serializer = Serializer(bounded);
        for (var i = 0; i < numbers.Count; i++)
        {
            var number = numbers[i];
            serializer.Write(_objects[number]); bounded.Text("\n");
            _entries[number] = new(_entries[number].Identifier, PdfCrossReferenceKind.Compressed, 0, identifier.Number, i, 0);
        }
        var dictionary = new PdfDictionary();
        dictionary[PdfName.Create("Type"u8)] = PdfName.Create("ObjStm"u8);
        dictionary[PdfName.Create("N"u8)] = PdfInteger.Create(numbers.Count);
        dictionary[PdfName.Create("First"u8)] = PdfInteger.Create(first);
        WriteIndirect(identifier, EncodedStream(dictionary, destination => destination.Write(raw.GetBuffer().AsSpan(0, (int)raw.Length))));
    }
    private PdfStream EncodedStream(PdfDictionary dictionary, Action<Stream> emit)
    {
        using var encoded = new MemoryStream();
        using var bounded = new PdfOutput(encoded, _cancellationToken, _options.MaxBufferedBytes);
        using (var compressor = new ZLibStream(bounded, CompressionLevel.SmallestSize, true)) { emit(compressor); }
        dictionary[PdfName.Create("Filter"u8)] = PdfName.Create("FlateDecode"u8);
        return PdfStream.Create(dictionary, BinarySegment.Create(encoded.ToArray()));
    }
}
