using System;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.Content.Pdf.Internal;

[global::System.Diagnostics.DebuggerDisplay("PdfObjectStream")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class PdfObjectStream
{
    private readonly ReadOnlyMemory<byte> _bytes;
    private readonly int[] _numbers;
    private readonly int[] _offsets;
    private readonly int _first;
    private readonly long _position;
    internal int Count => _numbers.Length;
    internal int Number(int index) => _numbers[index];
    internal PdfObjectStream(PdfStream stream, PdfReaderOptions options)
    {
        _position = stream.Position;
        if (!PdfFileLoader.IsName(stream.Dictionary.Get("Type"), "ObjStm"))
        {
            throw new ContentFormatException(ErrorMessages.StructureInvalid, ContentFormatErrorCode.InvalidStructure, _position);
        }
        var count = PdfFileLoader.RequiredInteger(stream.Dictionary, "N", _position);
        var first = PdfFileLoader.RequiredInteger(stream.Dictionary, "First", _position);
        if (count > options.MaxObjects || count > options.MaxBufferedBytes / 8)
        {
            throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, _position);
        }
        _bytes = stream.Decode(options);
        if (first > _bytes.Length || count > first / 4)
        {
            throw new ContentFormatException(ErrorMessages.StructureInvalid, ContentFormatErrorCode.InvalidStructure, _position);
        }
        _first = (int)first;
        _numbers = new int[(int)count]; _offsets = new int[(int)count];
        using var source = Assimalign.Cohesion.Content.Binary.BinarySegment.Create(_bytes).OpenRead();
        using var input = new PdfInput(source, 0, first, errorAnchor: _position);
        var parser = new PdfParser(input, options);
        var seen = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            var number = parser.ReadUnsigned(); var offset = parser.ReadUnsigned();
            if (number <= 0 || number > int.MaxValue || offset >= _bytes.Length - first || (i > 0 && offset <= _offsets[i - 1]) || !seen.Add((int)number))
            {
                throw new ContentFormatException(ErrorMessages.StructureInvalid, ContentFormatErrorCode.InvalidStructure, _position);
            }
            _numbers[i] = (int)number; _offsets[i] = (int)offset;
        }
        input.SkipWhiteSpace();
        if (input.Position != first) { throw new ContentFormatException(ErrorMessages.StructureInvalid, ContentFormatErrorCode.InvalidStructure, _position); }
    }
    internal IPdfObject Extract(int index, int number, PdfReaderOptions options, Func<bool> isEncrypted)
    {
        if (index < 0 || index >= _numbers.Length || _numbers[index] != number)
        {
            throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, _position);
        }
        var start = _first + _offsets[index];
        var end = index + 1 == _numbers.Length ? _bytes.Length : _first + _offsets[index + 1];
        using var source = Assimalign.Cohesion.Content.Binary.BinarySegment.Create(_bytes).OpenRead();
        using var input = new PdfInput(source, start, end, errorAnchor: _position);
        var parser = new PdfParser(input, options, isEncrypted: isEncrypted);
        var value = parser.ReadObject();
        input.SkipWhiteSpace();
        if (input.Position != end || value is PdfIndirectReference)
        {
            throw new ContentFormatException(ErrorMessages.StructureInvalid, ContentFormatErrorCode.InvalidStructure, _position);
        }
        return value;
    }

    private sealed class DebugView
    {
        public DebugView(PdfObjectStream value)
        {
        }
    }
}
