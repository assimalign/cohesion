using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Pdf.Internal;

internal sealed class PdfFileLoader
{
    private readonly Stream _source;
    private readonly long _origin;
    private readonly PdfReaderOptions _options;
    private readonly CancellationToken _cancellationToken;
    private readonly PdfSecurityState _security;
    internal PdfFileLoader(Stream source, long origin, PdfReaderOptions options, CancellationToken cancellationToken = default, PdfSecurityState? security = null)
    {
        _source = source; _origin = origin; _options = options; _cancellationToken = cancellationToken;
        _security = security ?? new PdfSecurityState();
    }
    private long Absolute(long position) => position + (_source is ContentSliceStream slice ? slice.SourceOffset : 0);
    internal PdfDocument Load(bool leaveOpen)
    {
        var version = ReadHeader();
        Dictionary<int, PdfCrossReferenceEntry> entries;
        PdfDictionary trailer;
        long startXref;
        try
        {
            startXref = FindStartXref();
            (entries, trailer) = ReadCrossReferences(startXref);
        }
        catch (ContentFormatException error) when (_options.IsLenient && error.ErrorCode is ContentFormatErrorCode.InvalidStructure or ContentFormatErrorCode.Truncated or ContentFormatErrorCode.InvalidValue)
        {
            (entries, trailer) = Rebuild(); startXref = -1;
        }
        catch (ContentFormatException error) when (error.ErrorCode is ContentFormatErrorCode.Truncated or ContentFormatErrorCode.InvalidValue)
        {
            throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, error.Position ?? Absolute(_origin), error);
        }
        _security.IsEncrypted = trailer.Get("Encrypt") is not (null or PdfNull);
        var document = new PdfDocument(_source, _origin, version, trailer, entries, startXref, leaveOpen);
        document.Initialize(_options, _security, _cancellationToken);
        return document;
    }
    private Version ReadHeader()
    {
        var count = (int)Math.Min(1032, _source.Length - _origin);
        var bytes = BinarySegment.Create(_source, _origin, count).ToArray(1032);
        for (var i = 0; i < 1024 && i + 8 <= bytes.Length; i++)
        {
            if (!bytes.AsSpan(i, 5).SequenceEqual("%PDF-"u8)) { continue; }
            if (bytes[i + 6] != '.' || bytes[i + 5] is < (byte)'0' or > (byte)'9' || bytes[i + 7] is < (byte)'0' or > (byte)'9' ||
                (i + 8 < bytes.Length && !PdfInput.IsWhiteSpace(bytes[i + 8]))) { break; }
            var major = bytes[i + 5] - '0'; var minor = bytes[i + 7] - '0';
            if ((major == 1 && minor <= 7) || (major == 2 && minor == 0)) { return new Version(major, minor); }
            throw new ContentFormatException(ErrorMessages.HeaderInvalid, ContentFormatErrorCode.Unsupported, Absolute(_origin + i));
        }
        throw new ContentFormatException(ErrorMessages.HeaderInvalid, ContentFormatErrorCode.InvalidStructure, Absolute(_origin));
    }
    private long FindStartXref()
    {
        var length = (int)Math.Min(_options.StartXrefSearchBytes, _source.Length - _origin);
        var start = _source.Length - length;
        var bytes = BinarySegment.Create(_source, start, length).ToArray(_options.MaxBufferedBytes);
        for (var i = length - 9; i >= 0; i--)
        {
            if (!bytes.AsSpan(i, 9).SequenceEqual("startxref"u8) || (i > 0 && !PdfInput.IsWhiteSpace(bytes[i - 1]))) { continue; }
            using var input = new PdfInput(_source, start + i, cancellationToken: _cancellationToken);
            var parser = new PdfParser(input, _options);
            parser.Expect("startxref");
            var offset = parser.ReadUnsigned();
            while (PdfInput.IsWhiteSpace(input.Peek())) { input.Read(); }
            if (!parser.TryKeyword("%%EOF")) { throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, input.AbsolutePosition); }
            if (!_options.IsLenient)
            {
                while (PdfInput.IsWhiteSpace(input.Peek())) { input.Read(); }
                if (input.Peek() != -1) { throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, input.AbsolutePosition); }
            }
            return offset;
        }
        throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, Absolute(_source.Length));
    }
    private (Dictionary<int, PdfCrossReferenceEntry>, PdfDictionary) ReadCrossReferences(long start)
    {
        var entries = new Dictionary<int, PdfCrossReferenceEntry>();
        var trailer = new PdfDictionary();
        var visited = new HashSet<long>();
        long? offset = start;
        while (offset.HasValue)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            Visit(offset.Value, visited);
            var (section, dictionary) = ReadSection(offset.Value, false);
            // In a hybrid revision the supplemental stream wins over its companion table.
            if (OptionalOffset(dictionary, "XRefStm") is long hybrid)
            {
                Visit(hybrid, visited);
                var (supplement, _) = ReadSection(hybrid, true);
                foreach (var pair in supplement) { section[pair.Key] = pair.Value; }
            }
            foreach (var pair in section)
            {
                if (!entries.ContainsKey(pair.Key))
                {
                    if (entries.Count >= _options.MaxObjects) { throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, Absolute(_origin + offset.Value)); }
                    entries.Add(pair.Key, pair.Value);
                }
            }
            foreach (var pair in dictionary) { if (!trailer.ContainsKey(pair.Key)) { trailer[pair.Key] = pair.Value; } }
            offset = OptionalOffset(dictionary, "Prev");
        }
        var size = RequiredInteger(trailer, "Size", Absolute(_origin));
        if (size <= 0 || size > (long)int.MaxValue + 1) { throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, Absolute(_origin)); }
        foreach (var number in entries.Keys)
        {
            if (number >= size) { throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, Absolute(_origin)); }
        }
        if (trailer.Get("Root") is not PdfIndirectReference || !entries.TryGetValue(0, out var head) || head.Kind != PdfCrossReferenceKind.Free || head.Identifier.Generation != 65535)
        {
            throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, Absolute(_origin));
        }
        return (entries, trailer);
    }
    private void Visit(long offset, HashSet<long> visited)
    {
        if (offset < 0 || offset >= _source.Length - _origin || !visited.Add(offset))
        {
            throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, Absolute(_origin + Math.Clamp(offset, 0, _source.Length - _origin)));
        }
        if (visited.Count > _options.MaxCrossReferenceSections)
        {
            throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, Absolute(_origin + offset));
        }
    }
    private (Dictionary<int, PdfCrossReferenceEntry>, PdfDictionary) ReadSection(long offset, bool streamOnly)
    {
        using var input = new PdfInput(_source, _origin + offset, cancellationToken: _cancellationToken);
        var parser = new PdfParser(input, _options, isEncrypted: () => _security.IsEncrypted);
        try
        {
            if (!streamOnly && parser.TryKeyword("xref")) { return ReadTable(parser); }
            var (_, value) = parser.ReadIndirect();
            if (value is not PdfStream stream || !IsName(stream.Dictionary.Get("Type"), "XRef"))
            {
                throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, input.AbsolutePosition);
            }
            return (ReadXrefStream(stream), stream.Dictionary);
        }
        catch (ContentFormatException error) when (error.ErrorCode is ContentFormatErrorCode.Truncated or ContentFormatErrorCode.InvalidValue)
        {
            throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, error.Position ?? input.AbsolutePosition, error);
        }
    }
    private (Dictionary<int, PdfCrossReferenceEntry>, PdfDictionary) ReadTable(PdfParser parser)
    {
        var entries = new Dictionary<int, PdfCrossReferenceEntry>();
        var input = parser.Input;
        Span<byte> row = stackalloc byte[20];
        while (true)
        {
            input.SkipWhiteSpace();
            if (parser.TryKeyword("trailer")) { break; }
            var first = parser.ReadUnsigned(); var count = parser.ReadUnsigned();
            if (first > int.MaxValue || count > (long)int.MaxValue + 1 - first)
            {
                throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, input.AbsolutePosition);
            }
            if (count > _options.MaxObjects - entries.Count) { throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, input.AbsolutePosition); }
            input.SkipWhiteSpace();
            for (var index = 0; index < count; index++)
            {
                var position = input.AbsolutePosition;
                long field; long generation; bool free;
                if (_options.IsLenient)
                {
                    field = parser.ReadUnsigned(); generation = parser.ReadUnsigned();
                    input.SkipWhiteSpace();
                    free = parser.TryKeyword("f");
                    if (!free) { parser.Expect("n"); }
                    input.SkipWhiteSpace();
                }
                else
                {
                    for (var j = 0; j < 20; j++) { row[j] = (byte)input.Required(); }
                    if (row[10] != 32 || row[16] != 32 || row[17] is not ((byte)'f' or (byte)'n') ||
                        !((row[18] == 13 && row[19] == 10) || (row[18] == 32 && row[19] is 10 or 13)))
                    {
                        throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, position);
                    }
                    field = Decimal(row[..10], position); generation = Decimal(row.Slice(11, 5), position); free = row[17] == 'f';
                }
                var number = (int)(first + index);
                var entry = Entry(number, free ? 0UL : 1UL, (ulong)field, (ulong)generation, position);
                if (!entries.TryAdd(number, entry)) { throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, position); }
            }
        }
        if (parser.ReadObject() is not PdfDictionary dictionary) { throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, input.AbsolutePosition); }
        return (entries, dictionary);
    }
    private Dictionary<int, PdfCrossReferenceEntry> ReadXrefStream(PdfStream stream)
    {
        var position = stream.Position;
        var dictionary = stream.Dictionary;
        var size = RequiredInteger(dictionary, "Size", position);
        if (size <= 0 || size > (long)int.MaxValue + 1 || dictionary.Get("W") is not PdfArray widths || widths.Count != 3)
        {
            throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, position);
        }
        Span<int> width = stackalloc int[3];
        var rowLength = 0;
        for (var i = 0; i < 3; i++)
        {
            if (widths[i] is not PdfInteger value || value.Value is < 0 or > 8) { throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, position); }
            width[i] = (int)value.Value; rowLength += width[i];
        }
        if (rowLength == 0) { throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, position); }
        var index = dictionary.Get("Index") as PdfArray;
        if (dictionary.Get("Index") is not null && index is null || index is not null && (index.Count == 0 || index.Count % 2 != 0))
        {
            throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, position);
        }
        var ranges = new List<(int First, int Count)>();
        long total = 0, previousEnd = 0;
        var rangeCount = index is null ? 1 : index.Count / 2;
        for (var i = 0; i < rangeCount; i++)
        {
            var first = index is null ? 0 : Integer(index[i * 2], position);
            var count = index is null ? size : Integer(index[i * 2 + 1], position);
            if (first < previousEnd || first > int.MaxValue || count < 0 || count > size - first) { throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, position); }
            if (count > _options.MaxObjects - total) { throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, position); }
            ranges.Add(((int)first, (int)count)); total += count; previousEnd = first + count;
        }
        if (total * rowLength > _options.MaxDecodedBytes) { throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, position); }
        // Xref stream entries, including filter parameters, must be direct (ÃƒÆ’Ã†â€™ÃƒÂ¢Ã¢â€šÂ¬Ã…Â¡ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â§7.5.8.2).
        RequireDirect(dictionary.Get("Filter"), position, 0);
        RequireDirect(dictionary.Get("DecodeParms"), position, 0);
        var decoded = stream.Decode(_options);
        if (decoded.Length != total * rowLength) { throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, position); }
        var reader = new BinaryContentReader(decoded.Span);
        var entries = new Dictionary<int, PdfCrossReferenceEntry>();
        foreach (var range in ranges)
        {
            for (var i = 0; i < range.Count; i++)
            {
                var type = width[0] == 0 ? 1UL : reader.ReadUIntBigEndian(width[0]);
                var field1 = reader.ReadUIntBigEndian(width[1]); var field2 = reader.ReadUIntBigEndian(width[2]);
                entries.Add(range.First + i, Entry(range.First + i, type, field1, field2, position));
            }
        }
        return entries;
    }
    private void RequireDirect(IPdfObject? value, long position, int depth)
    {
        if (depth > _options.MaxDepth) { throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, position); }
        if (value is PdfIndirectReference) { throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, position); }
        if (value is PdfArray array) { foreach (var item in array) { RequireDirect(item, position, depth + 1); } }
        if (value is PdfDictionary dictionary) { foreach (var pair in dictionary) { RequireDirect(pair.Value, position, depth + 1); } }
    }
    private PdfCrossReferenceEntry Entry(int number, ulong type, ulong field1, ulong field2, long position)
    {
        if (type > 2) { throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.Unsupported, position); }
        if ((type != 2 && field2 > 65535) || (type != 1 && field1 > int.MaxValue) || (type == 2 && (field1 == 0 || field2 > int.MaxValue)) ||
            (type == 1 && (field1 == 0 || field1 >= (ulong)(_source.Length - _origin))) || (number == 0 && (type != 0 || field2 != 65535)))
        {
            throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, position);
        }
        return new(PdfObjectIdentifier.Create(number, type == 2 ? 0 : (int)field2), (PdfCrossReferenceKind)type,
            type == 1 ? (long)field1 : 0, type == 2 ? (int)field1 : 0, type == 2 ? (int)field2 : 0, type == 0 ? (int)field1 : 0);
    }
    internal (Dictionary<int, PdfCrossReferenceEntry>, PdfDictionary) Rebuild()
    {
        if (_source.Length - _origin > _options.MaxRecoveryScanBytes) { throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, Absolute(_origin)); }
        var entries = new Dictionary<int, PdfCrossReferenceEntry>();
        var trailer = new PdfDictionary();
        PdfIndirectReference? catalog = null;
        using var input = new PdfInput(_source, _origin, cancellationToken: _cancellationToken);
        while (input.Position < input.End)
        {
            input.SkipWhiteSpace();
            var start = input.Position;
            var parser = new PdfParser(input, _options, isEncrypted: () => _security.IsEncrypted);
            try
            {
                if (parser.TryKeyword("trailer"))
                {
                    if (parser.ReadObject() is PdfDictionary found) { foreach (var pair in found) { trailer[pair.Key] = pair.Value; } }
                    continue;
                }
                if (input.Peek() is >= '0' and <= '9')
                {
                    var (id, value) = parser.ReadIndirect();
                    if (entries.Count >= _options.MaxObjects && !entries.ContainsKey(id.Number)) { throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, input.AbsolutePosition); }
                    entries[id.Number] = new(id, PdfCrossReferenceKind.Uncompressed, start - _origin, 0, 0, 0);
                    if (value is PdfDictionary dictionary && IsName(dictionary.Get("Type"), "Catalog")) { catalog = PdfIndirectReference.Create(id); }
                    if (value is PdfStream stream && IsName(stream.Dictionary.Get("Type"), "XRef"))
                    {
                        foreach (var key in new[] { "Root", "Encrypt", "Info", "ID", "Size" })
                        {
                            if (stream.Dictionary.Get(key) is IPdfObject item) { trailer[PdfName.Create(Encoding.ASCII.GetBytes(key))] = item; }
                        }
                    }
                    if (value is PdfStream objectStream && IsName(objectStream.Dictionary.Get("Type"), "ObjStm"))
                    {
                        var index = new PdfObjectStream(objectStream, _options);
                        for (var i = 0; i < index.Count; i++)
                        {
                            var number = index.Number(i);
                            if (!entries.ContainsKey(number) && entries.Count >= _options.MaxObjects)
                            {
                                throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, input.AbsolutePosition);
                            }
                            entries[number] = new(PdfObjectIdentifier.Create(number), PdfCrossReferenceKind.Compressed, 0, id.Number, i, 0);
                        }
                    }
                    continue; // Skip complete objects, especially payloads that contain apparent object headers.
                }
                // Skip complete lexical tokens, so n g obj inside a literal or comment is not discovered.
                var token = new PdfLexer(input, _options).Read();
                if (token.Kind == PdfTokenKind.End) { break; }
                continue;
            }
            catch (ContentFormatException error) when (error.ErrorCode is ContentFormatErrorCode.InvalidStructure or ContentFormatErrorCode.InvalidValue or ContentFormatErrorCode.Truncated) { }
            input.Position = Math.Min(start + 1, input.End);
        }
        if ((trailer.Get("Root") is not PdfIndirectReference root || !entries.ContainsKey(root.Identifier.Number)) && catalog is not null) { trailer[PdfName.Create("Root"u8)] = catalog; }
        if (trailer.Get("Root") is not PdfIndirectReference || entries.Count == 0) { throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, Absolute(_origin)); }
        var highest = 0;
        foreach (var number in entries.Keys) { highest = Math.Max(highest, number); }
        trailer[PdfName.Create("Size"u8)] = PdfInteger.Create((long)highest + 1);
        return (entries, trailer);
    }
    private static long Decimal(ReadOnlySpan<byte> bytes, long position)
    {
        long value = 0;
        foreach (var digit in bytes)
        {
            if (digit is < (byte)'0' or > (byte)'9') { throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, position); }
            value = value * 10 + digit - '0';
        }
        return value;
    }
    private long? OptionalOffset(PdfDictionary dictionary, string name)
    {
        var value = dictionary.Get(name);
        return value is null ? null : Integer(value, Absolute(_origin));
    }
    internal static long RequiredInteger(PdfDictionary dictionary, string name, long position) => Integer(dictionary.Get(name), position);
    internal static long Integer(IPdfObject? value, long position)
    {
        if (value is not PdfInteger integer || integer.Value < 0) { throw new ContentFormatException(ErrorMessages.StructureInvalid, ContentFormatErrorCode.InvalidStructure, position); }
        return integer.Value;
    }
    internal static bool IsName(IPdfObject? value, string name) => value is PdfName pdfName && Encoding.ASCII.GetString(pdfName.Bytes.Span) == name;
}
