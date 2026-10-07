using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// How a request head that HPACK cannot decode is reported (#1322): a field block that cannot be
/// decompressed is a connection error of type <c>COMPRESSION_ERROR</c> (RFC 9113 §4.3), while a block
/// that decodes but breaks a field rule stays a <c>PROTOCOL_ERROR</c>. The whole block is decoded before
/// any field is judged, so a decoding failure is reported as one even after a malformed field.
/// </summary>
public class Http2HPackDecodingErrorTests
{
    private const string IndexPastTable = "an index past the dynamic table";
    private const string IndexZero = "the index zero";
    private const string HuffmanPadding = "Huffman padding longer than seven bits";
    private const string HuffmanPaddingNotOnes = "Huffman padding that is not all 1 bits";
    private const string HuffmanEndOfString = "a Huffman EOS symbol";
    private const string IntegerOverflow = "an integer that overflows 31 bits";
    private const string StringPastBlock = "a string length past the end of the block";
    private const string LateSizeUpdate = "a dynamic table size update after a field line";
    private const string OversizedSizeUpdate = "a dynamic table size update over the advertised 4096 octets";

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 HPACK Errors: A request head HPACK cannot decode should close the connection with COMPRESSION_ERROR")]
    [InlineData(IndexPastTable)]
    [InlineData(IndexZero)]
    [InlineData(HuffmanPadding)]
    [InlineData(HuffmanPaddingNotOnes)]
    [InlineData(HuffmanEndOfString)]
    [InlineData(IntegerOverflow)]
    [InlineData(StringPastBlock)]
    [InlineData(LateSizeUpdate)]
    [InlineData(OversizedSizeUpdate)]
    public async Task ReceiveAsync_OnUndecodableRequestHead_ShouldGoAwayCompressionError(string failure)
    {
        // Arrange
        byte[] block = UndecodableBlock(failure);

        // Act
        (int dispatched, byte[] output) = await ReceiveRequestHeadAsync(block);

        // Assert — RFC 9113 §4.3 / RFC 7541 §2.3.3, §5.1, §5.2, §4.2, §6.3.
        dispatched.ShouldBe(0, failure);
        Http2TestSettings.AssertContainsGoAway(output, Http2ErrorCode.CompressionError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 HPACK Errors: A request head that breaks a field rule should keep PROTOCOL_ERROR")]
    public async Task ReceiveAsync_OnDecodableHeadWithMalformedField_ShouldGoAwayProtocolError()
    {
        // Arrange — the block decodes; RFC 9113 §8.2.1 rejects the uppercase name.
        byte[] block = HPackTestEncoder.Block(
            HPackTestEncoder.RequestHead("GET", "/"),
            HPackTestEncoder.Literal("User-Agent", "tests"));

        // Act
        (int dispatched, byte[] output) = await ReceiveRequestHeadAsync(block);

        // Assert
        dispatched.ShouldBe(0);
        Http2TestSettings.AssertContainsGoAway(output, Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 HPACK Errors: A block that breaks a field rule and then fails to decode should report COMPRESSION_ERROR")]
    public async Task ReceiveAsync_OnMalformedFieldBeforeDecodingFailure_ShouldGoAwayCompressionError()
    {
        // Arrange — the uppercase name comes first; the index past the table follows it.
        byte[] block = HPackTestEncoder.Block(
            HPackTestEncoder.RequestHead("GET", "/"),
            HPackTestEncoder.Literal("User-Agent", "tests"),
            HPackTestEncoder.Indexed(70));

        // Act
        (int dispatched, byte[] output) = await ReceiveRequestHeadAsync(block);

        // Assert
        dispatched.ShouldBe(0);
        Http2TestSettings.AssertContainsGoAway(output, Http2ErrorCode.CompressionError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 HPACK Errors: Dynamic table size updates at the start of a block should be accepted")]
    public async Task ReceiveAsync_OnSizeUpdatesBeforeFirstFieldLine_ShouldDispatchRequest()
    {
        // Arrange — RFC 7541 §4.2: up to two updates (here 0, then 4096) may open a field block.
        byte[] block = HPackTestEncoder.Block(
            new byte[] { 0x20 },
            new byte[] { 0x3F, 0xE1, 0x1F },
            HPackTestEncoder.RequestHead("GET", "/resized"));

        // Act
        (int dispatched, byte[] output) = await ReceiveRequestHeadAsync(block);

        // Assert
        dispatched.ShouldBe(1);
        HttpProtocolPayloadFactory.ParseHttp2Frames(output).ShouldNotContain(frame => frame.FrameType == 0x7);
    }

    /// <summary>A request head that is valid up to the representation <paramref name="failure"/> names.</summary>
    private static byte[] UndecodableBlock(string failure)
    {
        byte[] head = HPackTestEncoder.RequestHead("GET", "/");

        return failure switch
        {
            // RFC 7541 §2.3.3 — 61 static entries and an empty dynamic table: index 70 is past both.
            IndexPastTable => HPackTestEncoder.Block(head, HPackTestEncoder.Indexed(70)),
            // RFC 7541 §6.1 — the index zero is not used.
            IndexZero => HPackTestEncoder.Block(head, new byte[] { 0x80 }),
            // RFC 7541 §5.2 — a literal name "a" whose value is one Huffman octet of eight 1 bits.
            HuffmanPadding => HPackTestEncoder.Block(head, new byte[] { 0x00, 0x01, (byte)'a', 0x81, 0xFF }),
            // RFC 7541 §5.2 — "a" is 00011, so 0x1E pads it with 110: padding must be the EOS prefix, 111.
            HuffmanPaddingNotOnes => HPackTestEncoder.Block(head, new byte[] { 0x00, 0x01, (byte)'a', 0x81, 0x1E }),
            // RFC 7541 §5.2 — 32 1 bits hold the 30-bit EOS code, which a string must not contain.
            HuffmanEndOfString => HPackTestEncoder.Block(head, new byte[] { 0x00, 0x01, (byte)'a', 0x84, 0xFF, 0xFF, 0xFF, 0xFF }),
            // RFC 7541 §5.1 — an indexed field whose index needs more than 31 bits.
            IntegerOverflow => HPackTestEncoder.Block(head, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F }),
            // RFC 7541 §5.2 — a literal name 2^31 - 1 octets long, in a block of a few dozen.
            StringPastBlock => HPackTestEncoder.Block(head, new byte[] { 0x00, 0x7F, 0x80, 0xFF, 0xFF, 0xFF, 0x07 }),
            // RFC 7541 §4.2 — a size update (to 0) may only open a field block.
            LateSizeUpdate => HPackTestEncoder.Block(head, new byte[] { 0x20 }),
            // RFC 7541 §6.3 — 5000 octets is over the SETTINGS_HEADER_TABLE_SIZE the server advertised.
            OversizedSizeUpdate => HPackTestEncoder.Block(new byte[] { 0x3F, 0xE9, 0x26 }, head),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown decoding failure."),
        };
    }

    /// <summary>
    /// Sends <paramref name="block"/> as the head of stream 1 on a fresh connection, drains the receive
    /// loop, and returns how many requests it dispatched and what the server wrote.
    /// </summary>
    private static async Task<(int Dispatched, byte[] Output)> ReceiveRequestHeadAsync(byte[] block)
    {
        byte[][] wire =
        [
            Http2TestSettings.Preface(),
            Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>()),
            HPackTestEncoder.HeadersFrame(1, endStream: true, block),
        ];
        TestConnection connection = new(wire.SelectMany(part => part).ToArray());
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));
        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        int dispatched = 0;

        await foreach (IHttpContext context in connectionContext.ReceiveAsync())
        {
            dispatched++;
            await connectionContext.SendAsync(context);
        }

        return (dispatched, await connection.ReadOutputAsync());
    }
}
