using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary.Tests;

namespace Assimalign.Cohesion.Content.Ebml.Tests;

public class EbmlVIntReadTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§4–6: Read: decodes a stream-backed VInt of each width")]
    [InlineData(0ul, 1)]
    [InlineData(126ul, 1)]
    [InlineData(300ul, 2)]
    [InlineData(0xFFFFul, 3)]
    [InlineData(0xdeffadul, 4)]
    public void Read_FromStream_DecodesValueAndWidth(ulong value, int expectedLength)
    {
        // Arrange
        var source = new MemoryStream(Encode(value));

        // Act
        var actual = VInt.Read(source, 8, new byte[8]);

        // Assert
        actual.Value.ShouldBe(value);
        actual.Length.ShouldBe(expectedLength);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§4–6: Read: reassembles a VInt delivered one byte per read")]
    public void Read_WhenStreamReturnsPartialReads_ReadsUntilComplete()
    {
        // Arrange — a 4-byte VInt dripped one byte at a time forces the reader to
        // keep reading rather than trusting a single Read call to fill the buffer.
        using var source = new ChunkedReadStream(new NonSeekableStream(new MemoryStream(Encode(0xdeffad))), maxRead: 1);

        // Act
        var actual = VInt.Read(source, 8, new byte[8]);

        // Assert
        actual.Value.ShouldBe(0xdeffadul);
        actual.Length.ShouldBe(4);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§4–6: Read: empty stream reports classified truncation")]
    public void Read_FromEmptyStream_ShouldReportTruncation()
    {
        // Arrange
        var source = new MemoryStream([]);

        // Act / Assert
        var error = Should.Throw<EbmlDataFormatException>(() => VInt.Read(source, 8, new byte[8]));
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
        error.Position.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§4–6: Read: truncated trailing octets report classified truncation")]
    public void Read_WhenTrailingOctetsAreTruncated_ShouldReportTruncation()
    {
        // Arrange — width marker announces 4 bytes but only 2 are present.
        var encoded = Encode(0xdeffad);
        var source = new MemoryStream([encoded[0], encoded[1]]);

        // Act / Assert
        var error = Should.Throw<EbmlDataFormatException>(() => VInt.Read(source, 8, new byte[8]));
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
        error.Position.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§4–6: Read: rejects a VInt wider than the caller's maximum")]
    public void Read_WhenWiderThanMaxLength_ShouldThrowEbmlDataFormat()
    {
        // Arrange — a 4-byte VInt offered to a reader that accepts at most 2.
        var source = new MemoryStream(Encode(0xdeffad));

        // Act / Assert
        Should.Throw<EbmlDataFormatException>(() => VInt.Read(source, 2, new byte[2]));
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§4–6: Write then Read: round-trips the encoded value")]
    [InlineData(0ul)]
    [InlineData(127ul)]
    [InlineData(300ul)]
    [InlineData(0xFFFFul)]
    [InlineData(0xdeffadul)]
    public void Write_ThenRead_RoundTripsValue(ulong value)
    {
        // Arrange
        var stream = new MemoryStream();
        var written = VInt.EncodeSize(value).Write(stream);
        stream.Position = 0;

        // Act
        var actual = VInt.Read(stream, 8, new byte[8]);

        // Assert
        actual.Value.ShouldBe(value);
        actual.Length.ShouldBe(written);
    }

    private static byte[] Encode(ulong value)
    {
        var stream = new MemoryStream();
        VInt.EncodeSize(value).Write(stream);
        return stream.ToArray();
    }
}
