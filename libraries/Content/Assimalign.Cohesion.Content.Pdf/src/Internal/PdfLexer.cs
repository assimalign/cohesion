using System;

namespace Assimalign.Cohesion.Content.Pdf.Internal;

[global::System.Diagnostics.DebuggerDisplay("PdfLexer")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class PdfLexer
{
    internal PdfInput Input { get; }
    private readonly PdfReaderOptions _options;
    internal PdfLexer(PdfInput input, PdfReaderOptions options) { Input = input; _options = options; }
    internal PdfToken Read()
    {
        Input.SkipWhiteSpace();
        var position = Input.AbsolutePosition;
        var value = Input.Read();
        if (value == -1) { return new(PdfTokenKind.End, Array.Empty<byte>(), position); }
        switch (value)
        {
            case '[': return new(PdfTokenKind.ArrayStart, Array.Empty<byte>(), position);
            case ']': return new(PdfTokenKind.ArrayEnd, Array.Empty<byte>(), position);
            case '(' : return Literal(position);
            case '<':
                if (Input.Peek() == '<') { Input.Read(); return new(PdfTokenKind.DictionaryStart, Array.Empty<byte>(), position); }
                return Hexadecimal(position);
            case '>':
                if (Input.Required() == '>') { return new(PdfTokenKind.DictionaryEnd, Array.Empty<byte>(), position); }
                break;
            case ')': break;
            default:
                using (var bytes = new PdfBuffer(_options.MaxBufferedBytes, position))
                {
                    bytes.Add((byte)value);
                    while (!PdfInput.IsDelimiter(Input.Peek())) { bytes.Add((byte)Input.Read()); }
                    return new(value == '/' ? PdfTokenKind.Name : PdfTokenKind.Word, bytes.ToArray(), position);
                }
        }
        throw new ContentFormatException(ErrorMessages.TokenInvalid, ContentFormatErrorCode.InvalidStructure, position);
    }
    private PdfToken Literal(long position)
    {
        using var bytes = new PdfBuffer(_options.MaxBufferedBytes, position);
        var depth = 1;
        while (depth > 0)
        {
            var value = Input.Required();
            if (value == '\\')
            {
                value = Input.Required();
                if (value == 13) { if (Input.Peek() == 10) { Input.Read(); } continue; }
                if (value == 10) { continue; }
                if (value is >= '0' and <= '7')
                {
                    var octal = value - '0';
                    for (var i = 0; i < 2 && Input.Peek() is >= '0' and <= '7'; i++) { octal = (octal << 3) | (Input.Read() - '0'); }
                    bytes.Add((byte)octal);
                    continue;
                }
                value = value switch { 'n' => 10, 'r' => 13, 't' => 9, 'b' => 8, 'f' => 12, _ => value };
            }
            else if (value == '(')
            {
                if (++depth > _options.MaxDepth) { throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, position); }
            }
            else if (value == ')') { if (--depth == 0) { break; } }
            else if (value == 13) { if (Input.Peek() == 10) { Input.Read(); } value = 10; }
            bytes.Add((byte)value);
        }
        return new(PdfTokenKind.Literal, bytes.ToArray(), position);
    }
    private PdfToken Hexadecimal(long position)
    {
        using var bytes = new PdfBuffer(_options.MaxBufferedBytes, position);
        var high = -1;
        while (true)
        {
            var value = Input.Required();
            if (value == '>') { break; }
            if (PdfInput.IsWhiteSpace(value)) { continue; }
            var digit = PdfName.Hex(value);
            if (digit < 0) { throw new ContentFormatException(ErrorMessages.TokenInvalid, ContentFormatErrorCode.InvalidValue, Input.AbsolutePosition - 1); }
            if (high < 0) { high = digit; }
            else { bytes.Add((byte)((high << 4) | digit)); high = -1; }
        }
        if (high >= 0) { bytes.Add((byte)(high << 4)); }
        return new(PdfTokenKind.Hexadecimal, bytes.ToArray(), position);
    }

    private sealed class DebugView
    {
        public DebugView(PdfLexer value)
        {
        }
    }
}
