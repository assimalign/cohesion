using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal.Http2.HPack;
using Assimalign.Cohesion.Http.Connections.Internal.Http3;
using Assimalign.Cohesion.Http.Connections.Internal.Http3.Frames;
using Assimalign.Cohesion.Http.Connections.Internal.Http3.QPack;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

public class QPackDynamicTests
{
    // ---- Huffman encoder (RFC 7541 Appendix B, shared HPACK/QPACK) ----

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Huffman: Encoder round-trips through the decoder")]
    [InlineData("www.example.com")]
    [InlineData("custom-key")]
    [InlineData("no-cache")]
    [InlineData("Mon, 21 Oct 2013 20:13:21 GMT")]
    [InlineData("")]
    public void HuffmanEncoder_RoundTripsThroughDecoder(string value)
    {
        byte[] octets = Encoding.Latin1.GetBytes(value);

        byte[] encoded = HPackHuffmanEncoder.Encode(octets);
        byte[] decoded = encoded.Length == 0 ? [] : HPackHuffmanDecoder.Decode(encoded);

        Encoding.Latin1.GetString(decoded).ShouldBe(value);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Huffman: Encoder matches RFC 7541 C.4.1 example")]
    public void HuffmanEncoder_MatchesRfcExample()
    {
        // RFC 7541 Appendix C.4.1 — the Huffman encoding of "www.example.com".
        byte[] expected = [0xf1, 0xe3, 0xc2, 0xe5, 0xf2, 0x3a, 0x6b, 0xa0, 0xab, 0x90, 0xf4, 0xff];

        byte[] encoded = HPackHuffmanEncoder.Encode(Encoding.Latin1.GetBytes("www.example.com"));

        encoded.ShouldBe(expected);
        HPackHuffmanEncoder.GetEncodedLength(Encoding.Latin1.GetBytes("www.example.com")).ShouldBe(12);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Huffman: QPACK string codec emits Huffman only when shorter")]
    public void QPackStringCodec_EncodeShortest_ChoosesHuffmanWhenShorter()
    {
        // A compressible string beats its raw octets → Huffman flag (bit 7) set.
        using MemoryStream compressible = new();
        QPackStringCodec.EncodeShortest(compressible, "application/json", 7, 0x00);
        byte[] compressed = compressible.ToArray();
        (compressed[0] & 0x80).ShouldNotBe(0);
        int i = 0;
        QPackStringCodec.Decode(compressed, ref i, 7).ShouldBe("application/json");

        // A single character cannot beat one raw octet → Huffman flag clear.
        using MemoryStream tiny = new();
        QPackStringCodec.EncodeShortest(tiny, "v", 7, 0x00);
        byte[] rawTiny = tiny.ToArray();
        (rawTiny[0] & 0x80).ShouldBe(0);
        int j = 0;
        QPackStringCodec.Decode(rawTiny, ref j, 7).ShouldBe("v");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Huffman: QPACK field-section encode round-trips Huffman values")]
    public void QPackEncoder_HuffmanValues_RoundTrip()
    {
        byte[] encoded = QPackFieldSectionEncoder.Encode([("accept", "application/json"), ("x-a", "v")]);

        List<(string Name, string Value)> decoded = QPackFieldSectionDecoder.Decode(encoded);

        decoded.ShouldContain(("accept", "application/json"));
        decoded.ShouldContain(("x-a", "v"));
    }

    // ---- Dynamic table (RFC 9204 §3.2) ----

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - QPackTable: Insert accounts size and insert count")]
    public void Table_InsertLiteral_AccountsSizeAndCount()
    {
        QPackDynamicTable table = new(4096);
        table.SetCapacity(4096);

        table.InsertWithLiteralName("custom-key", "custom-value");

        table.InsertCount.ShouldBe(1);
        // Entry size = name(10) + value(12) + 32 overhead.
        table.Size.ShouldBe(54);
        table.TryGetByAbsoluteIndex(0, out string name, out string value).ShouldBeTrue();
        name.ShouldBe("custom-key");
        value.ShouldBe("custom-value");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - QPackTable: Insert with static name reference resolves the name")]
    public void Table_InsertWithStaticName_ResolvesName()
    {
        QPackDynamicTable table = new(4096);
        table.SetCapacity(4096);

        // Static index 44 = content-type (name only used).
        table.InsertWithStaticNameReference(44, "text/csv");

        table.TryGetByAbsoluteIndex(0, out string name, out string value).ShouldBeTrue();
        name.ShouldBe("content-type");
        value.ShouldBe("text/csv");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - QPackTable: Duplicate copies an entry as the newest")]
    public void Table_Duplicate_CopiesEntry()
    {
        QPackDynamicTable table = new(4096);
        table.SetCapacity(4096);
        table.InsertWithLiteralName("a", "1");
        table.InsertWithLiteralName("b", "2");

        // Duplicate relative index 1 (the older "a: 1").
        table.Duplicate(1);

        table.InsertCount.ShouldBe(3);
        table.TryGetByAbsoluteIndex(2, out string name, out string value).ShouldBeTrue();
        name.ShouldBe("a");
        value.ShouldBe("1");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - QPackTable: Insert with dynamic name reference reuses a live name")]
    public void Table_InsertWithDynamicName_ReusesName()
    {
        QPackDynamicTable table = new(4096);
        table.SetCapacity(4096);
        table.InsertWithLiteralName("x-token", "one");

        // Relative index 0 = most recent (x-token); reuse its name with a new value.
        table.InsertWithDynamicNameReference(0, "two");

        table.TryGetByAbsoluteIndex(1, out string name, out string value).ShouldBeTrue();
        name.ShouldBe("x-token");
        value.ShouldBe("two");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - QPackTable: Insert evicts the oldest entry to fit")]
    public void Table_Insert_EvictsOldestToFit()
    {
        // Capacity fits exactly two 33-octet entries (name 1 + value 0 + 32).
        QPackDynamicTable table = new(66);
        table.SetCapacity(66);
        table.InsertWithLiteralName("a", "");
        table.InsertWithLiteralName("b", "");

        // A third insert evicts "a" (absolute index 0).
        table.InsertWithLiteralName("c", "");

        table.InsertCount.ShouldBe(3);
        table.TryGetByAbsoluteIndex(0, out _, out _).ShouldBeFalse(); // Evicted.
        table.TryGetByAbsoluteIndex(1, out string bName, out _).ShouldBeTrue();
        bName.ShouldBe("b");
        table.TryGetByAbsoluteIndex(2, out string cName, out _).ShouldBeTrue();
        cName.ShouldBe("c");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - QPackTable: Oversized insert is an encoder stream error")]
    public void Table_Insert_Oversized_Throws()
    {
        QPackDynamicTable table = new(64);
        table.SetCapacity(64);

        // name(40) + value(0) + 32 = 72 > capacity 64.
        QPackException ex = Should.Throw<QPackException>(() => table.InsertWithLiteralName(new string('x', 40), ""));
        ex.ErrorCode.ShouldBe(Http3ErrorCode.QPackEncoderStreamError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - QPackTable: Capacity above the advertised maximum is an error")]
    public void Table_SetCapacity_AboveMaximum_Throws()
    {
        QPackDynamicTable table = new(4096);

        QPackException ex = Should.Throw<QPackException>(() => table.SetCapacity(4097));
        ex.ErrorCode.ShouldBe(Http3ErrorCode.QPackEncoderStreamError);
    }

    // ---- Encoder instruction parser (RFC 9204 §4.3) ----

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - QPackEncoderInstr: Applies capacity + insert + duplicate")]
    public void EncoderInstructions_Apply_MutateTable()
    {
        QPackDynamicTable table = new(4096);

        byte[] stream = Concat(
            HttpProtocolPayloadFactory.QPackSetCapacity(4096),
            HttpProtocolPayloadFactory.QPackInsertWithLiteralName("x-a", "1"),
            HttpProtocolPayloadFactory.QPackInsertWithStaticName(44, "text/html"),
            HttpProtocolPayloadFactory.QPackDuplicate(1));

        int consumed = 0;
        int inserts = 0;
        while (QPackEncoderInstructionParser.TryApplyNext(stream.AsSpan(consumed), table, out int used, out bool inserted))
        {
            consumed += used;
            if (inserted)
            {
                inserts++;
            }
        }

        consumed.ShouldBe(stream.Length);
        inserts.ShouldBe(3);
        table.Capacity.ShouldBe(4096);
        table.InsertCount.ShouldBe(3);
        table.TryGetByAbsoluteIndex(1, out string ctName, out string ctValue).ShouldBeTrue();
        ctName.ShouldBe("content-type");
        ctValue.ShouldBe("text/html");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - QPackEncoderInstr: Incomplete instruction consumes nothing")]
    public void EncoderInstructions_Incomplete_ConsumesNothing()
    {
        QPackDynamicTable table = new(4096);
        table.SetCapacity(4096);

        byte[] full = HttpProtocolPayloadFactory.QPackInsertWithLiteralName("x-header", "value");

        // Feed all but the last octet — the value string is truncated.
        QPackEncoderInstructionParser.TryApplyNext(full.AsSpan(0, full.Length - 1), table, out int consumed, out bool inserted)
            .ShouldBeFalse();
        consumed.ShouldBe(0);
        inserted.ShouldBeFalse();
        table.InsertCount.ShouldBe(0);
    }

    // ---- Decoder instruction encoder (RFC 9204 §4.4) ----

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - QPackDecoderInstr: Section Acknowledgment byte format")]
    [InlineData(0, new byte[] { 0x80 })]
    [InlineData(5, new byte[] { 0x85 })]
    [InlineData(127, new byte[] { 0xFF, 0x00 })]
    public void DecoderInstructions_SectionAck_ByteFormat(long streamId, byte[] expected)
    {
        QPackDecoderInstructionEncoder.SectionAcknowledgment(streamId).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - QPackDecoderInstr: Insert Count Increment byte format")]
    [InlineData(1, new byte[] { 0x01 })]
    [InlineData(10, new byte[] { 0x0A })]
    [InlineData(63, new byte[] { 0x3F, 0x00 })]
    public void DecoderInstructions_InsertCountIncrement_ByteFormat(long increment, byte[] expected)
    {
        QPackDecoderInstructionEncoder.InsertCountIncrement(increment).ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - QPackDecoderInstr: Stream Cancellation byte format")]
    public void DecoderInstructions_StreamCancellation_ByteFormat()
    {
        // 0 1 + 6-bit stream id.
        QPackDecoderInstructionEncoder.StreamCancellation(4).ShouldBe(new byte[] { 0x44 });
    }

    // ---- Required Insert Count + Base (RFC 9204 §4.5.1) ----

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - QPackPrefix: Required Insert Count reconstructs")]
    [InlineData(0, 128, 0, 0)]      // Encoded 0 → RIC 0.
    [InlineData(2, 128, 1, 1)]      // RIC 1 within window.
    [InlineData(4, 128, 3, 3)]      // RIC 3 within window.
    [InlineData(2, 128, 257, 257)]  // Wrapped: RIC 257 (fullRange 256).
    public void Prefix_DecodeRequiredInsertCount_Reconstructs(long encoded, long maxEntries, long totalInserts, long expected)
    {
        QPackFieldSectionPrefix.DecodeRequiredInsertCount(encoded, maxEntries, totalInserts).ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - QPackPrefix: Base honors the sign bit")]
    public void Prefix_Parse_BaseHonorsSign()
    {
        // Encoded RIC 3 (→ RIC 2 with MaxEntries 128), S = 0, DeltaBase 1 → Base 3.
        QPackFieldSectionPrefix positive = QPackFieldSectionPrefix.Parse([0x03, 0x01], 4096, 2);
        positive.RequiredInsertCount.ShouldBe(2);
        positive.Base.ShouldBe(3);

        // Same RIC, S = 1, DeltaBase 1 → Base = RIC - 1 - 1 = 0.
        QPackFieldSectionPrefix negative = QPackFieldSectionPrefix.Parse([0x03, 0x81], 4096, 2);
        negative.Base.ShouldBe(0);
    }

    // ---- Field-section dynamic decode (RFC 9204 §4.5) ----

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - QPackDecode: Resolves a dynamic indexed field line")]
    public void Decode_DynamicIndexedFieldLine_Resolves()
    {
        QPackDynamicTable table = new(4096);
        table.SetCapacity(4096);
        table.InsertWithLiteralName("x-dyn", "hello"); // Absolute index 0, insert count 1.

        // Prefix: Encoded RIC 2 (RIC 1), Delta Base 0 (S=0 → Base 1).
        // Field line: dynamic indexed, relative 0 → absolute Base-1-0 = 0.
        byte[] section = [0x02, 0x00, 0x80];

        List<(string Name, string Value)> fields = QPackFieldSectionDecoder.Decode(section, table);

        fields.ShouldHaveSingleItem();
        fields[0].ShouldBe(("x-dyn", "hello"));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - QPackDecode: Resolves a post-base indexed field line")]
    public void Decode_PostBaseIndexedFieldLine_Resolves()
    {
        QPackDynamicTable table = new(4096);
        table.SetCapacity(4096);
        table.InsertWithLiteralName("x-pb", "pbvalue"); // Absolute index 0, insert count 1.

        // Prefix: Encoded RIC 2 (RIC 1), Delta Base with S=1, DeltaBase 0 → Base 0.
        // Post-base index 0 → absolute Base + 0 = 0.
        byte[] section = [0x02, 0x80, 0x10];

        List<(string Name, string Value)> fields = QPackFieldSectionDecoder.Decode(section, table);

        fields.ShouldHaveSingleItem();
        fields[0].ShouldBe(("x-pb", "pbvalue"));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - QPackDecode: Unresolvable dynamic reference is decompression failure")]
    public void Decode_MissingDynamicEntry_ThrowsDecompressionFailed()
    {
        QPackDynamicTable table = new(4096);
        table.SetCapacity(4096);
        // No insertions, but a field section claims RIC 1 and references entry 0.
        byte[] section = [0x02, 0x00, 0x80];

        QPackException ex = Should.Throw<QPackException>(() => QPackFieldSectionDecoder.Decode(section, table));
        ex.ErrorCode.ShouldBe(Http3ErrorCode.QPackDecompressionFailed);
    }

    // ---- Decoder state coordination ----

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - QPackState: Applies encoder instructions and reports insertions")]
    public void State_ApplyEncoderInstructions_ReportsInsertions()
    {
        QPackDecoderState state = new(new Http3QPackOptions { MaxTableCapacity = 4096, MaxBlockedStreams = 8 });

        byte[] stream = Concat(
            HttpProtocolPayloadFactory.QPackSetCapacity(4096),
            HttpProtocolPayloadFactory.QPackInsertWithLiteralName("x-a", "1"),
            HttpProtocolPayloadFactory.QPackInsertWithLiteralName("x-b", "2"));

        int consumed = state.ApplyEncoderInstructions(stream, out int insertions);

        consumed.ShouldBe(stream.Length);
        insertions.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - QPackState: Static (RIC 0) request decodes without blocking")]
    public async Task State_DecodeRequest_StaticSection_DecodesWithoutBlocking()
    {
        QPackDecoderState state = new(new Http3QPackOptions { MaxTableCapacity = 4096, MaxBlockedStreams = 8 });

        // Prefix [0,0] + a literal field line (:path /).
        byte[] section = HttpProtocolPayloadFactory.CreateHttp3FieldSection((":path", "/"));

        QPackDecodeResult result = await state.DecodeRequestAsync(section, CancellationToken.None);

        result.ReferencedDynamicTable.ShouldBeFalse();
        result.Fields.ShouldContain((":path", "/"));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - QPackState: Blocked stream beyond the limit fails decompression")]
    public async Task State_DecodeRequest_ExceedsBlockedStreams_Throws()
    {
        // Blocked-stream budget 0: any section that must block fails immediately.
        QPackDecoderState state = new(new Http3QPackOptions { MaxTableCapacity = 4096, MaxBlockedStreams = 0 });

        // RIC 1 referencing dynamic entry 0, but no insertions have arrived.
        byte[] section = [0x02, 0x00, 0x80];

        QPackException ex = await Should.ThrowAsync<QPackException>(
            () => state.DecodeRequestAsync(section, CancellationToken.None));
        ex.ErrorCode.ShouldBe(Http3ErrorCode.QPackDecompressionFailed);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - QPackState: A blocked request unblocks once insertions arrive")]
    public async Task State_DecodeRequest_UnblocksWhenInsertionsArrive()
    {
        QPackDecoderState state = new(new Http3QPackOptions { MaxTableCapacity = 4096, MaxBlockedStreams = 8 });

        // RIC 1 dynamic indexed reference to entry 0, before any insertion.
        byte[] section = [0x02, 0x00, 0x80];
        Task<QPackDecodeResult> decode = state.DecodeRequestAsync(section, CancellationToken.None);

        // The decode is blocked until the entry is inserted.
        decode.IsCompleted.ShouldBeFalse();

        state.ApplyEncoderInstructions(
            Concat(
                HttpProtocolPayloadFactory.QPackSetCapacity(4096),
                HttpProtocolPayloadFactory.QPackInsertWithLiteralName("x-dyn", "v")),
            out int insertions);
        insertions.ShouldBe(1);

        QPackDecodeResult result = await decode.WaitAsync(System.TimeSpan.FromSeconds(5));
        result.ReferencedDynamicTable.ShouldBeTrue();
        result.Fields.ShouldContain(("x-dyn", "v"));
    }

    private static byte[] Concat(params byte[][] parts)
    {
        List<byte> all = new();
        foreach (byte[] part in parts)
        {
            all.AddRange(part);
        }

        return all.ToArray();
    }
}
