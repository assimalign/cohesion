using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Pdf.Internal;

internal sealed partial class PdfSaveSession
{
    private List<(int First, int Count)> Ranges(bool table = false)
    {
        var ranges = new List<(int First, int Count)>();
        foreach (var pair in _entries)
        {
            if (table && pair.Value.Kind == PdfCrossReferenceKind.Compressed) { continue; }
            if (ranges.Count != 0 && (long)ranges[^1].First + ranges[^1].Count == pair.Key)
            {
                var range = ranges[^1]; ranges[^1] = (range.First, range.Count + 1);
            }
            else { ranges.Add((pair.Key, 1)); }
        }
        return ranges;
    }
    private long WriteTable(PdfDictionary trailer)
    {
        var start = _output.Count;
        _output.Text("xref\n");
        foreach (var range in Ranges(table: true))
        {
            _output.Text(FormattableString.Invariant($"{range.First} {range.Count}\n"));
            for (var index = 0; index < range.Count; index++)
            {
                var entry = _entries[range.First + index];
                var field = entry.Kind == PdfCrossReferenceKind.Free ? entry.NextFreeObjectNumber : entry.Offset;
                if (field > 9_999_999_999)
                {
                    throw new ContentFormatException(ErrorMessages.TableOffsetLimit, ContentFormatErrorCode.LimitExceeded, _output.Count);
                }
                _output.Text(field.ToString("D10", CultureInfo.InvariantCulture)); _output.Text(" ");
                _output.Text(entry.Identifier.Generation.ToString("D5", CultureInfo.InvariantCulture));
                _output.Text(entry.Kind == PdfCrossReferenceKind.Free ? " f \n" : " n \n");
            }
        }
        trailer[PdfName.Create("Size"u8)] = PdfInteger.Create(_next);
        _output.Text("trailer\n"); _serializer.Write(trailer); _output.Text("\n");
        return start;
    }
    private long WriteCrossReferenceStream(PdfDictionary trailer)
    {
        var identifier = Allocate();
        var start = _output.Count;
        _entries[identifier.Number] = new(identifier, PdfCrossReferenceKind.Uncompressed, start, 0, 0, 0);
        if ((long)_entries.Count * 14 > _options.MaxBufferedBytes) { Limit(); }
        var dictionary = PdfObjectSerializer.CopyDictionary(trailer);
        dictionary[PdfName.Create("Size"u8)] = PdfInteger.Create(_next);
        dictionary[PdfName.Create("Type"u8)] = PdfName.Create("XRef"u8);
        var widths = new PdfArray(); widths.Add(PdfInteger.Create(1)); widths.Add(PdfInteger.Create(8)); widths.Add(PdfInteger.Create(4));
        dictionary[PdfName.Create("W"u8)] = widths;
        var indices = new PdfArray();
        foreach (var range in Ranges()) { indices.Add(PdfInteger.Create(range.First)); indices.Add(PdfInteger.Create(range.Count)); }
        dictionary[PdfName.Create("Index"u8)] = indices;
        var parameters = new PdfDictionary();
        parameters[PdfName.Create("Predictor"u8)] = PdfInteger.Create(12);
        parameters[PdfName.Create("Columns"u8)] = PdfInteger.Create(13);
        dictionary[PdfName.Create("DecodeParms"u8)] = parameters;
        var stream = EncodedStream(dictionary, destination =>
        {
            var row = new ArrayBufferWriter<byte>(13);
            Span<byte> previous = stackalloc byte[13]; previous.Clear();
            Span<byte> predicted = stackalloc byte[14]; predicted[0] = 2;
            foreach (var entry in _entries.Values)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                row.Clear(); var writer = new BinaryContentWriter(row);
                writer.WriteByte(entry.Kind == PdfCrossReferenceKind.Free ? (byte)0 : entry.Kind == PdfCrossReferenceKind.Uncompressed ? (byte)1 : (byte)2);
                writer.WriteUInt64BigEndian((ulong)(entry.Kind == PdfCrossReferenceKind.Free ? entry.NextFreeObjectNumber :
                    entry.Kind == PdfCrossReferenceKind.Uncompressed ? entry.Offset : entry.ObjectStreamNumber));
                writer.WriteUInt32BigEndian((uint)(entry.Kind == PdfCrossReferenceKind.Compressed ? entry.ObjectStreamIndex : entry.Identifier.Generation));
                for (var i = 0; i < 13; i++) { predicted[i + 1] = unchecked((byte)(row.WrittenSpan[i] - previous[i])); }
                destination.Write(predicted); row.WrittenSpan.CopyTo(previous);
            }
        });
        WriteIndirect(identifier, stream);
        return start;
    }
}
