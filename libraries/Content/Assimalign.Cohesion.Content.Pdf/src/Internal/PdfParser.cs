using System;
using System.Globalization;
using System.Text;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Pdf.Internal;

internal sealed class PdfParser
{
    private readonly PdfLexer _lexer;
    private readonly PdfReaderOptions _options;
    private readonly Func<PdfObjectIdentifier, IPdfObject>? _resolve;
    private readonly Func<bool>? _isEncrypted;
    private int _nodes;
    internal PdfInput Input => _lexer.Input;
    internal PdfParser(PdfInput input, PdfReaderOptions options, Func<PdfObjectIdentifier, IPdfObject>? resolve = null, Func<bool>? isEncrypted = null)
    {
        _lexer = new(input, options); _options = options; _resolve = resolve; _isEncrypted = isEncrypted;
    }
    internal IPdfObject ReadObject(int depth = 0) => ReadObject(_lexer.Read(), depth);
    private IPdfObject ReadObject(PdfToken token, int depth)
    {
        if (depth > _options.MaxDepth || ++_nodes > _options.MaxObjects)
        {
            throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, token.Position);
        }
        switch (token.Kind)
        {
            case PdfTokenKind.End:
                throw new ContentFormatException(ErrorMessages.InputTruncated, ContentFormatErrorCode.Truncated, token.Position);
            case PdfTokenKind.Name:
                try { return PdfName.Parse(token.Bytes); }
                catch (ContentFormatException error)
                {
                    throw new ContentFormatException(ErrorMessages.NameInvalid, error.ErrorCode, token.Position + (error.Position ?? 0));
                }
            case PdfTokenKind.Literal: return new PdfString(token.Bytes, false, _isEncrypted, token.Position);
            case PdfTokenKind.Hexadecimal: return new PdfString(token.Bytes, true, _isEncrypted, token.Position);
            case PdfTokenKind.ArrayStart:
                var array = new PdfArray();
                while (true)
                {
                    var item = _lexer.Read();
                    if (item.Kind == PdfTokenKind.ArrayEnd) { return array; }
                    array.Add(ReadObject(item, depth + 1));
                }
            case PdfTokenKind.DictionaryStart:
                var dictionary = new PdfDictionary();
                while (true)
                {
                    var keyToken = _lexer.Read();
                    if (keyToken.Kind == PdfTokenKind.DictionaryEnd) { return dictionary; }
                    if (keyToken.Kind == PdfTokenKind.End) { throw new ContentFormatException(ErrorMessages.InputTruncated, ContentFormatErrorCode.Truncated, keyToken.Position); }
                    if (keyToken.Kind != PdfTokenKind.Name) { throw new ContentFormatException(ErrorMessages.StructureInvalid, ContentFormatErrorCode.InvalidStructure, keyToken.Position); }
                    var key = (PdfName)ReadObject(keyToken, depth + 1);
                    if (dictionary.ContainsKey(key) && !_options.IsLenient)
                    {
                        throw new ContentFormatException(ErrorMessages.StructureInvalid, ContentFormatErrorCode.InvalidStructure, keyToken.Position);
                    }
                    dictionary[key] = ReadObject(depth + 1);
                }
            case PdfTokenKind.Word:
                if (token.Is("null")) { return new PdfNull(); }
                if (token.Is("true")) { return PdfBoolean.Create(true); }
                if (token.Is("false")) { return PdfBoolean.Create(false); }
                var text = Encoding.ASCII.GetString(token.Bytes);
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                {
                    var afterNumber = Input.Position;
                    Input.SkipWhiteSpace();
                    if (Input.Peek() is >= '0' and <= '9')
                    {
                        var generation = _lexer.Read();
                        if (TryUnsigned(generation, out var gen) && gen <= 65535)
                        {
                            Input.SkipWhiteSpace();
                            if (Input.Peek() == 'R' && _lexer.Read().Is("R"))
                            {
                                if (integer <= 0 || integer > int.MaxValue) { throw new ContentFormatException(ErrorMessages.TokenInvalid, ContentFormatErrorCode.InvalidValue, token.Position); }
                                return PdfIndirectReference.Create(PdfObjectIdentifier.Create((int)integer, (int)gen));
                            }
                        }
                    }
                    Input.Position = afterNumber;
                    return PdfInteger.Create(integer);
                }
                var styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
                if (_options.IsLenient) { styles |= NumberStyles.AllowExponent; }
                // Integer overflow never silently changes an integer into a real.
                if ((text.Contains('.') || (_options.IsLenient && text.Contains('e', StringComparison.OrdinalIgnoreCase))) &&
                    double.TryParse(text, styles, CultureInfo.InvariantCulture, out var real) && double.IsFinite(real))
                {
                    return PdfReal.Create(real);
                }
                throw new ContentFormatException(ErrorMessages.TokenInvalid, ContentFormatErrorCode.InvalidValue, token.Position);
            default:
                throw new ContentFormatException(ErrorMessages.StructureInvalid, ContentFormatErrorCode.InvalidStructure, token.Position);
        }
    }
    internal (PdfObjectIdentifier Identifier, IPdfObject Value) ReadIndirect(PdfObjectIdentifier? expected = null)
    {
        var position = Input.AbsolutePosition;
        var number = ReadUnsigned();
        var generation = ReadUnsigned();
        Expect("obj");
        if (number <= 0 || number > int.MaxValue || generation > 65535)
        {
            throw new ContentFormatException(ErrorMessages.StructureInvalid, ContentFormatErrorCode.InvalidStructure, position);
        }
        var identifier = PdfObjectIdentifier.Create((int)number, (int)generation);
        if (expected.HasValue && expected.Value != identifier)
        {
            throw new ContentFormatException(ErrorMessages.XrefInvalid, ContentFormatErrorCode.InvalidStructure, position);
        }
        var value = ReadObject();
        var next = _lexer.Read();
        if (value is PdfDictionary dictionary && next.Is("stream"))
        {
            value = ReadStream(dictionary);
            next = _lexer.Read();
        }
        if (!next.Is("endobj"))
        {
            throw new ContentFormatException(ErrorMessages.StructureInvalid,
                next.Kind == PdfTokenKind.End ? ContentFormatErrorCode.Truncated : ContentFormatErrorCode.InvalidStructure, next.Position);
        }
        return (identifier, value);
    }
    private PdfStream ReadStream(PdfDictionary dictionary)
    {
        var eol = Input.Required();
        if (eol == 13)
        {
            if (Input.Peek() == 10) { Input.Read(); }
            else if (!_options.IsLenient) { throw new ContentFormatException(ErrorMessages.StructureInvalid, ContentFormatErrorCode.InvalidStructure, Input.AbsolutePosition); }
        }
        else if (eol != 10) { throw new ContentFormatException(ErrorMessages.StructureInvalid, ContentFormatErrorCode.InvalidStructure, Input.AbsolutePosition - 1); }
        var start = Input.Position;
        var absolute = Input.AbsolutePosition;
        var lengthObject = dictionary.Get("Length");
        if (lengthObject is PdfIndirectReference reference && _resolve is not null) { lengthObject = _resolve(reference.Identifier); }
        var length = lengthObject is PdfInteger size ? size.Value : -1;
        if (length >= 0 && length <= Input.End - start)
        {
            Input.Position = start + length;
            if (Input.Peek() == 13) { Input.Read(); if (Input.Peek() == 10) { Input.Read(); } }
            else if (Input.Peek() == 10) { Input.Read(); }
            if (TryKeyword("endstream")) { return new(dictionary, BinarySegment.Create(Input.Source, start, length), _isEncrypted, absolute); }
        }
        if (!_options.IsLenient)
        {
            throw new ContentFormatException(ErrorMessages.StreamLengthInvalid,
                length > Input.End - start || Input.Position == Input.End ? ContentFormatErrorCode.Truncated : ContentFormatErrorCode.InvalidStructure, absolute);
        }
        // Recovery accepts a line-start endstream followed by endobj. It is deliberately opt-in:
        // arbitrary encoded bytes can contain this sequence, so no heuristic can be lossless.
        Input.Position = start;
        var previous = 10;
        while (Input.Position < Input.End)
        {
            if (Input.Position - start >= _options.MaxRecoveryScanBytes)
            {
                throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, Input.AbsolutePosition);
            }
            var candidate = Input.Position;
            if (previous is 10 or 13 && Input.Peek() == 'e' && TryKeyword("endstream"))
            {
                var end = Input.Position;
                if (_lexer.Read().Is("endobj"))
                {
                    length = candidate - start;
                    if (length > 0)
                    {
                        Input.Position = candidate - 1;
                        var last = Input.Read();
                        if (last is 10 or 13) { length--; }
                        if (last == 10 && length > 0)
                        {
                            Input.Position = start + length - 1;
                            if (Input.Read() == 13) { length--; }
                        }
                    }
                    Input.Position = end;
                    return new(dictionary, BinarySegment.Create(Input.Source, start, length), _isEncrypted, absolute);
                }
            }
            Input.Position = candidate;
            previous = Input.Read();
        }
        throw new ContentFormatException(ErrorMessages.InputTruncated, ContentFormatErrorCode.Truncated, Input.AbsolutePosition);
    }
    internal bool TryKeyword(string keyword)
    {
        var start = Input.Position;
        foreach (var character in keyword)
        {
            if (Input.Read() != character) { Input.Position = start; return false; }
        }
        if (!PdfInput.IsDelimiter(Input.Peek())) { Input.Position = start; return false; }
        return true;
    }
    internal void Expect(string keyword)
    {
        var token = _lexer.Read();
        if (!token.Is(keyword))
        {
            throw new ContentFormatException(ErrorMessages.StructureInvalid,
                token.Kind == PdfTokenKind.End ? ContentFormatErrorCode.Truncated : ContentFormatErrorCode.InvalidStructure, token.Position);
        }
    }
    internal long ReadUnsigned()
    {
        var token = _lexer.Read();
        if (!TryUnsigned(token, out var result))
        {
            throw new ContentFormatException(ErrorMessages.StructureInvalid,
                token.Kind == PdfTokenKind.End ? ContentFormatErrorCode.Truncated : ContentFormatErrorCode.InvalidStructure, token.Position);
        }
        return result;
    }
    private static bool TryUnsigned(PdfToken token, out long value) =>
        long.TryParse(Encoding.ASCII.GetString(token.Bytes), NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
