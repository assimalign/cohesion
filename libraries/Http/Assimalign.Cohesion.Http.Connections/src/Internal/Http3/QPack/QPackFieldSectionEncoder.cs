using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3.QPack;

/// <summary>
/// Encodes name/value field lines into a QPACK encoded field section
/// (RFC 9204 §4.5) for the supported feature set. The encoder never uses
/// the dynamic table: it emits a zero Field Section Prefix and references
/// only the static table, falling back to literal representations. Field
/// names are emitted lowercase (RFC 9114 §4.2). Literal names and values are
/// Huffman-coded (RFC 9204 §4.1.2, RFC 7541 Appendix B) when the Huffman form
/// is strictly shorter than the raw octets; otherwise raw octets are emitted.
/// </summary>
/// <remarks>
/// The encoder stays static-only by design: an encoder is never required to
/// use the dynamic table, so responses reference the static table or literals
/// and never insert. This keeps response encoding stateless and means the
/// server never needs to track a decoder's acknowledgments to encode safely
/// (RFC 9204 §2.1.1). The <em>decoder</em> is what maintains the dynamic table
/// for inbound request field sections.
/// </remarks>
internal static class QPackFieldSectionEncoder
{
    // Representation pattern bits (RFC 9204 §4.5).
    private const byte IndexedStatic = 0b1100_0000;        // §4.5.2: 1 T(=1) index(6)
    private const byte LiteralNameRefStatic = 0b0101_0000; // §4.5.4: 0 1 N(=0) T(=1) name(4)
    private const byte LiteralLiteralName = 0b0010_0000;   // §4.5.6: 0 0 1 N(=0) H(=0) len(3)

    /// <summary>
    /// Encodes the supplied field lines, in order, into a fresh array.
    /// </summary>
    /// <param name="fields">The lowercase-name field lines to encode.</param>
    /// <returns>The encoded field section (prefix + field lines).</returns>
    public static byte[] Encode(IEnumerable<(string Name, string Value)> fields)
    {
        using MemoryStream buffer = new();

        // Field Section Prefix (RFC 9204 §4.5.1): Required Insert Count = 0,
        // S = 0, Delta Base = 0 — no dynamic-table references.
        buffer.WriteByte(0x00);
        buffer.WriteByte(0x00);

        foreach ((string name, string value) in fields)
        {
            string lowered = name.ToLowerInvariant();
            WriteField(buffer, lowered, value);
        }

        return buffer.ToArray();
    }

    private static void WriteField(Stream destination, string name, string value)
    {
        if (QPackStaticTable.TryGetFieldIndex(name, value, out int fieldIndex))
        {
            // §4.5.2 Indexed Field Line (static).
            QPackPrefixedInteger.Encode(destination, fieldIndex, 6, IndexedStatic);
            return;
        }

        if (QPackStaticTable.TryGetNameIndex(name, out int nameIndex))
        {
            // §4.5.4 Literal Field Line with (static) Name Reference.
            QPackPrefixedInteger.Encode(destination, nameIndex, 4, LiteralNameRefStatic);
            QPackStringCodec.EncodeShortest(destination, value, 7, 0x00);
            return;
        }

        // §4.5.6 Literal Field Line with Literal Name.
        QPackStringCodec.EncodeShortest(destination, name, 3, LiteralLiteralName);
        QPackStringCodec.EncodeShortest(destination, value, 7, 0x00);
    }
}
