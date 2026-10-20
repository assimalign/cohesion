using System;
using System.Text;
using System.Text.Encodings.Web;

namespace Assimalign.Cohesion.Database.Documents.Internal;

// RFC 8259 section 7 requires escaping quotes, backslashes and U+0000 through U+001F.
// The BCL relaxed encoder still escapes supplementary characters, so use the JSON set here.
internal sealed class DocumentJsonEncoder : JavaScriptEncoder
{
    private static readonly DocumentJsonEncoder _instance = new();

    private DocumentJsonEncoder() { }

    internal static DocumentJsonEncoder Instance => _instance;
    public override int MaxOutputCharactersPerInputCharacter => 6;

    public override bool WillEncode(int unicodeScalar)
        => unicodeScalar < 0x20 || unicodeScalar is '"' or '\\' || !Rune.IsValid(unicodeScalar);

    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
    {
        for (int i = 0; i < textLength; i++)
        {
            char value = text[i];
            if (value < 0x20 || value is '"' or '\\') { return i; }
            if (!char.IsSurrogate(value)) { continue; }
            if (!char.IsHighSurrogate(value) || i + 1 == textLength || !char.IsLowSurrogate(text[i + 1])) { return i; }
            i++;
        }
        return -1;
    }

    public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
    {
        numberOfCharactersWritten = 0;
        char escape = unicodeScalar switch
        {
            '"' => '"', '\\' => '\\', '\b' => 'b', '\t' => 't', '\n' => 'n', '\f' => 'f', '\r' => 'r', _ => '\0',
        };
        if (escape != '\0')
        {
            if (bufferLength < 2) { return false; }
            buffer[0] = '\\';
            buffer[1] = escape;
            numberOfCharactersWritten = 2;
            return true;
        }
        if (unicodeScalar is >= 0 and < 0x20)
        {
            if (bufferLength < 6) { return false; }
            const string hex = "0123456789abcdef";
            buffer[0] = '\\';
            buffer[1] = 'u';
            buffer[2] = buffer[3] = '0';
            buffer[4] = hex[unicodeScalar >> 4];
            buffer[5] = hex[unicodeScalar & 15];
            numberOfCharactersWritten = 6;
            return true;
        }
        var scalar = Rune.TryCreate(unicodeScalar, out var valid) ? valid : Rune.ReplacementChar;
        return scalar.TryEncodeToUtf16(new Span<char>(buffer, bufferLength), out numberOfCharactersWritten);
    }
}
