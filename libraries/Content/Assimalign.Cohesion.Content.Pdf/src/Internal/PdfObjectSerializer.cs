using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Pdf.Internal;

internal sealed class PdfObjectSerializer
{
    private readonly PdfOutput _output;
    private readonly int _maxDepth;
    private readonly int _maxObjects;
    private readonly HashSet<IPdfObject> _active = new(ReferenceEqualityComparer.Instance);
    private int _nodes;
    private readonly bool _compressModifiedStreams;
    private readonly bool _copyPayloads;
    private readonly CancellationToken _cancellationToken;
    private readonly Func<IPdfObject, IPdfObject>? _resolve;
    internal PdfObjectSerializer(PdfOutput output, int maxDepth = 128, int maxObjects = 1_000_000,
        bool compressModifiedStreams = false, bool copyPayloads = true, CancellationToken cancellationToken = default,
        Func<IPdfObject, IPdfObject>? resolve = null)
    {
        _output = output; _maxDepth = maxDepth; _maxObjects = maxObjects;
        _compressModifiedStreams = compressModifiedStreams; _copyPayloads = copyPayloads; _cancellationToken = cancellationToken;
        _resolve = resolve;
    }
    internal void Write(IPdfObject value) { _nodes = 0; WriteValue(value, 0); }
    private void WriteValue(IPdfObject value, int depth)
    {
        if (depth > _maxDepth || ++_nodes > _maxObjects)
        {
            throw new ContentFormatException(ErrorMessages.WriterLimit, ContentFormatErrorCode.LimitExceeded, _output.Count);
        }
        if (!_active.Add(value))
        {
            throw new ContentFormatException(ErrorMessages.DirectCycle, ContentFormatErrorCode.InvalidStructure, _output.Count);
        }
        try
        {
            switch (value)
            {
                case PdfNull: _output.Text("null"); break;
                case PdfBoolean boolean: _output.Text(boolean.Value ? "true" : "false"); break;
                case PdfInteger integer: _output.Text(integer.Value.ToString(CultureInfo.InvariantCulture)); break;
                case PdfReal real: _output.Text(Real(real.Value)); break;
                case PdfName name: WriteName(name); break;
                case PdfString text: WriteString(text); break;
                case PdfIndirectReference reference:
                    _output.Text(FormattableString.Invariant($"{reference.Identifier.Number} {reference.Identifier.Generation} R")); break;
                case PdfArray array:
                    _output.Text("[");
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (i != 0) { _output.Text(" "); }
                        WriteValue(array[i], depth + 1);
                    }
                    _output.Text("]"); break;
                case PdfDictionary dictionary: WriteDictionary(dictionary, depth); break;
                case PdfStream stream when depth == 0:
                    WriteStream(stream); break;
                default:
                    throw new ContentFormatException(ErrorMessages.ObjectUnsupported, ContentFormatErrorCode.Unsupported, _output.Count);
            }
        }
        finally { _active.Remove(value); }
    }
    private void WriteStream(PdfStream stream)
    {
        var copy = CopyDictionary(stream.Dictionary);
        var data = stream.Data;
        var compress = stream.IsDataReplaced && _compressModifiedStreams;
        var length = data.Length;
        if (stream.IsDataReplaced)
        {
            // Dictionary mutations cannot accidentally apply an old filter to replacement raw bytes.
            foreach (var key in new[] { "Filter", "DecodeParms", "F", "FFilter", "FDecodeParms", "DL" })
            {
                copy.Remove(PdfName.Create(System.Text.Encoding.ASCII.GetBytes(key)));
            }
        }
        if (compress)
        {
            copy[PdfName.Create("Filter"u8)] = PdfName.Create("FlateDecode"u8);
            if (_copyPayloads)
            {
                using var counter = new PdfOutput(Stream.Null, _cancellationToken);
                Compress(data, counter); length = counter.Count;
            }
        }
        IPdfObject lengthValue = PdfInteger.Create(length);
        if (!stream.IsDataReplaced && stream.Dictionary.Get("Length") is PdfIndirectReference reference &&
            _resolve?.Invoke(reference) is PdfInteger retainedLength && retainedLength.Value == length)
        {
            lengthValue = reference;
        }
        copy[PdfName.Create("Length"u8)] = lengthValue;
        WriteDictionary(copy, 0); _output.Text("\nstream\n");
        if (_copyPayloads)
        {
            if (compress) { Compress(data, _output); }
            else { Copy(data, _output); }
        }
        _output.Text("\nendstream");
    }
    private void Compress(BinarySegment data, Stream destination)
    {
        using var encoder = new ZLibStream(destination, CompressionLevel.SmallestSize, true);
        using var guarded = new PdfOutput(encoder, _cancellationToken);
        Copy(data, guarded);
    }
    private static void Copy(BinarySegment data, Stream destination)
    {
        using var source = data.OpenRead(); source.CopyExactlyTo(destination, data.Length);
    }
    private void WriteDictionary(PdfDictionary dictionary, int depth)
    {
        // Ordered dictionaries preserve intentional key order; no culture or hash-order dependence.
        _output.Text("<<");
        foreach (var pair in dictionary)
        {
            _output.Text(" "); WriteValue(pair.Key, depth + 1); _output.Text(" "); WriteValue(pair.Value, depth + 1);
        }
        _output.Text(" >>");
    }
    internal static PdfDictionary CopyDictionary(PdfDictionary dictionary)
    {
        var result = new PdfDictionary();
        foreach (var pair in dictionary) { result[pair.Key] = pair.Value; }
        return result;
    }
    private void WriteName(PdfName name)
    {
        _output.WriteByte((byte)'/');
        foreach (var value in name.Bytes.Span)
        {
            if (value is >= 33 and <= 126 && value is not ((byte)'#' or (byte)'/' or (byte)'%' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'))
            {
                _output.WriteByte(value);
            }
            else { _output.WriteByte((byte)'#'); Hex(value); }
        }
    }
    private void Hex(byte value)
    {
        const string digits = "0123456789ABCDEF";
        _output.WriteByte((byte)digits[value >> 4]); _output.WriteByte((byte)digits[value & 15]);
    }
    private void WriteString(PdfString text)
    {
        var bytes = text.Bytes.Span;
        var nonPrintable = 0;
        foreach (var value in bytes) { if (value is < 32 or > 126) { nonPrintable++; } }
        if (text.IsHexadecimal || nonPrintable > bytes.Length / 2)
        {
            _output.Text("<"); foreach (var value in bytes) { Hex(value); } _output.Text(">"); return;
        }
        _output.Text("(");
        foreach (var value in bytes)
        {
            switch (value)
            {
                case 10: _output.Text("\\n"); break;
                case 13: _output.Text("\\r"); break;
                case 9: _output.Text("\\t"); break;
                case 8: _output.Text("\\b"); break;
                case 12: _output.Text("\\f"); break;
                case (byte)'(': case (byte)')': case (byte)'\\':
                    _output.WriteByte((byte)'\\'); _output.WriteByte(value); break;
                case < 32 or > 126:
                    _output.WriteByte((byte)'\\');
                    _output.WriteByte((byte)('0' + (value >> 6)));
                    _output.WriteByte((byte)('0' + ((value >> 3) & 7)));
                    _output.WriteByte((byte)('0' + (value & 7))); break;
                default: _output.WriteByte(value); break;
            }
        }
        _output.Text(")");
    }
    internal static string Real(double value)
    {
        // Shortest round-trip Double spelling, expanded to PDF's exponent-free decimal grammar.
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var e = text.IndexOf('E');
        if (e >= 0)
        {
            var exponent = int.Parse(text.AsSpan(e + 1), CultureInfo.InvariantCulture);
            var sign = text[0] == '-' ? "-" : "";
            var mantissa = text[sign.Length..e];
            var dot = mantissa.IndexOf('.');
            var point = (dot < 0 ? mantissa.Length : dot) + exponent;
            var digits = mantissa.Replace(".", "", StringComparison.Ordinal);
            text = point <= 0 ? sign + "0." + new string('0', -point) + digits :
                point >= digits.Length ? sign + digits + new string('0', point - digits.Length) :
                sign + digits[..point] + "." + digits[point..];
        }
        // A decimal point preserves PdfReal even when its mathematical value is integral.
        if (!text.Contains('.', StringComparison.Ordinal)) { text += "."; }
        return text;
    }
}
