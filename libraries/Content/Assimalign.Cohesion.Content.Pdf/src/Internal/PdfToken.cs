namespace Assimalign.Cohesion.Content.Pdf.Internal;

internal enum PdfTokenKind { End, Word, Name, Literal, Hexadecimal, ArrayStart, ArrayEnd, DictionaryStart, DictionaryEnd }
internal readonly record struct PdfToken(PdfTokenKind Kind, byte[] Bytes, long Position)
{
    internal bool Is(string value) => Kind == PdfTokenKind.Word && System.Text.Encoding.ASCII.GetString(Bytes) == value;
}
