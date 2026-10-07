using System.IO;
using System.Text;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// Builds HPACK field blocks (RFC 7541) representation by representation, so a test controls exactly
/// which fields enter the decoder's dynamic table and which later blocks reference it. Strings are
/// written raw (H = 0), so every byte on the wire is the one the test wrote.
/// </summary>
internal static class HPackTestEncoder
{
    /// <summary>
    /// The index of the most recent dynamic-table entry: the static table holds indices 1 to 61
    /// (RFC 7541 Appendix A), so the dynamic table starts at 62 (RFC 7541 §2.3.3).
    /// </summary>
    public const int NewestDynamicIndex = 62;

    /// <summary>An Indexed Header Field (RFC 7541 §6.1) referencing <paramref name="index"/>.</summary>
    public static byte[] Indexed(int index)
    {
        using MemoryStream buffer = new();
        WriteInteger(buffer, index, 7, 0x80);
        return buffer.ToArray();
    }

    /// <summary>
    /// A Literal Header Field with Incremental Indexing and a literal name (RFC 7541 §6.2.1): the
    /// decoder adds the field to its dynamic table.
    /// </summary>
    public static byte[] LiteralWithIndexing(string name, string value) => LiteralField(0x40, 6, name, value);

    /// <summary>A Literal Header Field without Indexing and with a literal name (RFC 7541 §6.2.2).</summary>
    public static byte[] Literal(string name, string value) => LiteralField(0x00, 4, name, value);

    /// <summary>The literal field lines of a request head: the four pseudo-header fields, nothing indexed.</summary>
    public static byte[] RequestHead(string method, string path)
    {
        return Block(
            Literal(":method", method),
            Literal(":scheme", "https"),
            Literal(":path", path),
            Literal(":authority", "api.test"));
    }

    /// <summary>Concatenates field representations into one field block.</summary>
    public static byte[] Block(params byte[][] representations)
    {
        using MemoryStream buffer = new();

        foreach (byte[] representation in representations)
        {
            buffer.Write(representation, 0, representation.Length);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// A HEADERS frame (type 0x1) carrying <paramref name="block"/>, with END_HEADERS unless the block
    /// continues in CONTINUATION frames, and END_STREAM when requested.
    /// </summary>
    public static byte[] HeadersFrame(int streamId, bool endStream, byte[] block, bool endHeaders = true)
    {
        byte flags = (byte)((endHeaders ? 0x4 : 0x0) | (endStream ? 0x1 : 0x0));
        return Http2TestSettings.RawFrame(0x1, flags, streamId, block);
    }

    /// <summary>A CONTINUATION frame (type 0x9) carrying the next part of a field block.</summary>
    public static byte[] ContinuationFrame(int streamId, byte[] block, bool endHeaders)
        => Http2TestSettings.RawFrame(0x9, endHeaders ? (byte)0x4 : (byte)0x0, streamId, block);

    /// <summary>
    /// A trailer section: a HEADERS frame with END_STREAM and END_HEADERS (RFC 9113 §8.1).
    /// </summary>
    public static byte[] TrailersFrame(int streamId, params byte[][] representations)
        => HeadersFrame(streamId, endStream: true, Block(representations));

    private static byte[] LiteralField(byte pattern, int prefixLength, string name, string value)
    {
        using MemoryStream buffer = new();

        // Name index 0: the name follows as a string literal.
        WriteInteger(buffer, 0, prefixLength, pattern);
        WriteString(buffer, name);
        WriteString(buffer, value);
        return buffer.ToArray();
    }

    private static void WriteString(Stream stream, string value)
    {
        byte[] octets = Encoding.ASCII.GetBytes(value);
        WriteInteger(stream, octets.Length, 7, 0x00);
        stream.Write(octets, 0, octets.Length);
    }

    private static void WriteInteger(Stream stream, int value, int prefixLength, byte pattern)
    {
        // RFC 7541 §5.1 — an N-bit prefix, continued in 7-bit groups when the value does not fit.
        int maxPrefixValue = (1 << prefixLength) - 1;

        if (value < maxPrefixValue)
        {
            stream.WriteByte((byte)(pattern | value));
            return;
        }

        stream.WriteByte((byte)(pattern | maxPrefixValue));
        value -= maxPrefixValue;

        while (value >= 128)
        {
            stream.WriteByte((byte)((value % 128) + 128));
            value /= 128;
        }

        stream.WriteByte((byte)value);
    }
}
