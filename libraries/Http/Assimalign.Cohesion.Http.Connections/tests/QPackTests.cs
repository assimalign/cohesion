using System.Collections.Generic;
using System.IO;
using System.Linq;

using Assimalign.Cohesion.Http.Connections.Internal.Http3.QPack;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

public class QPackTests
{
    // ---- Static table (RFC 9204 Appendix A) ----

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - QPack: Static table has 99 entries")]
    public void StaticTable_Count_Is99()
    {
        QPackStaticTable.Count.ShouldBe(99);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Transports] - QPack: Static table resolves known indices")]
    [InlineData(0, ":authority", "")]
    [InlineData(1, ":path", "/")]
    [InlineData(17, ":method", "GET")]
    [InlineData(25, ":status", "200")]
    [InlineData(53, "content-type", "text/plain")]
    [InlineData(98, "x-frame-options", "sameorigin")]
    public void StaticTable_Get_ResolvesKnownEntries(int index, string expectedName, string expectedValue)
    {
        QPackStaticTable.TryGet(index, out string name, out string value).ShouldBeTrue();
        name.ShouldBe(expectedName);
        value.ShouldBe(expectedValue);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - QPack: Static table rejects out-of-range index")]
    public void StaticTable_Get_OutOfRange_ReturnsFalse()
    {
        QPackStaticTable.TryGet(99, out _, out _).ShouldBeFalse();
        QPackStaticTable.TryGet(-1, out _, out _).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Transports] - QPack: Static table finds exact field index")]
    [InlineData(":status", "200", 25)]
    [InlineData(":method", "GET", 17)]
    [InlineData("content-type", "text/plain", 53)]
    public void StaticTable_TryGetFieldIndex_FindsExactMatch(string name, string value, int expected)
    {
        QPackStaticTable.TryGetFieldIndex(name, value, out int index).ShouldBeTrue();
        index.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Transports] - QPack: Static table finds first name index")]
    [InlineData(":status", 24)]
    [InlineData(":method", 15)]
    [InlineData("content-type", 44)]
    [InlineData("content-length", 4)]
    public void StaticTable_TryGetNameIndex_FindsFirstName(string name, int expected)
    {
        QPackStaticTable.TryGetNameIndex(name, out int index).ShouldBeTrue();
        index.ShouldBe(expected);
    }

    // ---- Prefixed integers (RFC 9204 §4.1.1) ----

    [Theory(DisplayName = "Cohesion Test [Http.Transports] - QPack: Prefixed integer round-trips")]
    [InlineData(0, 7)]
    [InlineData(10, 7)]
    [InlineData(126, 7)]
    [InlineData(127, 7)]
    [InlineData(128, 7)]
    [InlineData(1337, 5)]
    [InlineData(0, 8)]
    [InlineData(255, 8)]
    [InlineData(100000, 6)]
    public void PrefixedInteger_RoundTrips(long value, int prefixBits)
    {
        using MemoryStream stream = new();
        QPackPrefixedInteger.Encode(stream, value, prefixBits, 0x00);
        byte[] encoded = stream.ToArray();

        int index = 0;
        long decoded = QPackPrefixedInteger.Decode(encoded, ref index, prefixBits);

        decoded.ShouldBe(value);
        index.ShouldBe(encoded.Length);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - QPack: Prefixed integer ignores high pattern bits")]
    public void PrefixedInteger_HighPatternBits_AreIgnored()
    {
        // First byte 0xC5 with a 6-bit prefix: value is the low 6 bits (5),
        // the 0x80/0x40 pattern bits are not part of the integer.
        byte[] encoded = [0xC5];
        int index = 0;
        QPackPrefixedInteger.Decode(encoded, ref index, 6).ShouldBe(5);
    }

    // ---- String literals (RFC 9204 §4.1.2) ----

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - QPack: Non-Huffman string round-trips")]
    public void String_NonHuffman_RoundTrips()
    {
        using MemoryStream stream = new();
        QPackStringCodec.Encode(stream, "text/plain", 7, 0x00);
        byte[] encoded = stream.ToArray();

        // Huffman flag (bit 7) must be clear for a raw literal.
        (encoded[0] & 0x80).ShouldBe(0);

        int index = 0;
        QPackStringCodec.Decode(encoded, ref index, 7).ShouldBe("text/plain");
        index.ShouldBe(encoded.Length);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - QPack: Huffman string decodes")]
    public void String_Huffman_Decodes()
    {
        byte[] huffman = HttpProtocolPayloadFactory.HuffmanEncode("www.example.com");

        using MemoryStream stream = new();
        // H flag set (bit 7 for a 7-bit prefix) + Huffman octets.
        QPackPrefixedInteger.Encode(stream, huffman.Length, 7, 0x80);
        stream.Write(huffman, 0, huffman.Length);
        byte[] encoded = stream.ToArray();

        int index = 0;
        QPackStringCodec.Decode(encoded, ref index, 7).ShouldBe("www.example.com");
        index.ShouldBe(encoded.Length);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - QPack: Truncated string throws")]
    public void String_Truncated_Throws()
    {
        // Declares length 5 but only 2 octets follow.
        byte[] encoded = [0x05, (byte)'a', (byte)'b'];
        int index = 0;
        Should.Throw<InvalidDataException>(() => QPackStringCodec.Decode(encoded, ref index, 7));
    }

    // ---- Field section decoder (RFC 9204 §4.5) ----

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - QPack: Decodes indexed static field line")]
    public void Decode_IndexedStaticFieldLine_ResolvesEntry()
    {
        // Prefix (0,0) + Indexed Field Line, static, index 25 (:status 200).
        byte[] section = [0x00, 0x00, 0xC0 | 25];

        List<(string Name, string Value)> fields = QPackFieldSectionDecoder.Decode(section);

        fields.ShouldHaveSingleItem();
        fields[0].ShouldBe((":status", "200"));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - QPack: Decodes literal with static name reference")]
    public void Decode_LiteralWithStaticNameReference_ResolvesName()
    {
        // Prefix + Literal w/ Name Reference (static, name index 4 = content-length) + value "42".
        byte[] section = [0x00, 0x00, 0x50 | 4, 0x02, (byte)'4', (byte)'2'];

        List<(string Name, string Value)> fields = QPackFieldSectionDecoder.Decode(section);

        fields.ShouldHaveSingleItem();
        fields[0].ShouldBe(("content-length", "42"));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - QPack: Decodes literal with literal name")]
    public void Decode_LiteralWithLiteralName_ReadsBoth()
    {
        using MemoryStream stream = new();
        stream.WriteByte(0x00);
        stream.WriteByte(0x00);
        // Literal name (3-bit prefix, pattern 001) + literal value (7-bit prefix).
        QPackStringCodec.Encode(stream, "x-custom", 3, 0b0010_0000);
        QPackStringCodec.Encode(stream, "hello", 7, 0x00);

        List<(string Name, string Value)> fields = QPackFieldSectionDecoder.Decode(stream.ToArray());

        fields.ShouldHaveSingleItem();
        fields[0].ShouldBe(("x-custom", "hello"));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - QPack: Rejects non-zero Required Insert Count")]
    public void Decode_NonZeroRequiredInsertCount_Throws()
    {
        // Required Insert Count = 1 — implies a dynamic-table reference.
        byte[] section = [0x01, 0x00, 0xC0 | 25];
        Should.Throw<InvalidDataException>(() => QPackFieldSectionDecoder.Decode(section));
    }

    [Theory(DisplayName = "Cohesion Test [Http.Transports] - QPack: Rejects dynamic and post-base references")]
    [InlineData(0x80)] // §4.5.2 Indexed Field Line, dynamic (T=0)
    [InlineData(0x40)] // §4.5.4 Literal w/ Name Reference, dynamic (T=0)
    [InlineData(0x10)] // §4.5.3 Indexed Field Line with Post-Base Index
    [InlineData(0x00)] // §4.5.5 Literal Field Line with Post-Base Name Reference
    public void Decode_DynamicOrPostBaseReference_Throws(int firstFieldByte)
    {
        byte[] section = [0x00, 0x00, (byte)firstFieldByte, 0x00, 0x00];
        Should.Throw<InvalidDataException>(() => QPackFieldSectionDecoder.Decode(section));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - QPack: Rejects out-of-range static index")]
    public void Decode_StaticIndexOutOfRange_Throws()
    {
        // Indexed Field Line, static, index 99 (one past the table): 0xFF then
        // continuation 99 - 63 = 36.
        byte[] section = [0x00, 0x00, 0xFF, 36];
        Should.Throw<InvalidDataException>(() => QPackFieldSectionDecoder.Decode(section));
    }

    // ---- Encoder + round-trip (RFC 9204 §4.5) ----

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - QPack: Encoder emits a zero field section prefix")]
    public void Encode_AlwaysEmitsZeroPrefix()
    {
        byte[] encoded = QPackFieldSectionEncoder.Encode([(":status", "200")]);

        encoded[0].ShouldBe((byte)0x00); // Required Insert Count
        encoded[1].ShouldBe((byte)0x00); // S + Delta Base
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - QPack: Encoder uses a static index for :status 200")]
    public void Encode_StatusOk_UsesStaticIndex()
    {
        byte[] encoded = QPackFieldSectionEncoder.Encode([(":status", "200")]);

        // Indexed Field Line, static, index 25.
        encoded.ShouldBe(new byte[] { 0x00, 0x00, 0xC0 | 25 });
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - QPack: Encoder lowercases field names")]
    public void Encode_UppercaseName_LowercasedOnWire()
    {
        byte[] encoded = QPackFieldSectionEncoder.Encode([("X-Custom", "V")]);

        List<(string Name, string Value)> decoded = QPackFieldSectionDecoder.Decode(encoded);
        decoded.ShouldHaveSingleItem();
        decoded[0].Name.ShouldBe("x-custom");
        decoded[0].Value.ShouldBe("V");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - QPack: Encoder and decoder round-trip mixed representations")]
    public void Encode_Decode_RoundTrips()
    {
        (string Name, string Value)[] fields =
        [
            (":status", "200"),
            ("content-type", "text/plain"),
            ("content-length", "11"),
            ("x-custom-header", "custom value"),
        ];

        byte[] encoded = QPackFieldSectionEncoder.Encode(fields);
        List<(string Name, string Value)> decoded = QPackFieldSectionDecoder.Decode(encoded);

        decoded.ShouldBe(fields.ToList());
    }
}
