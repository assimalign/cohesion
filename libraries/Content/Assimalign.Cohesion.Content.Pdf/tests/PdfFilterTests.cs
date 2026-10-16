using System;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Pdf.Tests;

public sealed class PdfFilterTests
{
    internal static PdfStream Stream(string filter, byte[] bytes, PdfDictionary? parameters = null)
    {
        var dictionary = new PdfDictionary { [PdfName.Create("Filter"u8)] = PdfName.Create(Encoding.ASCII.GetBytes(filter)) };
        if (parameters is not null) { dictionary[PdfName.Create("DecodeParms"u8)] = parameters; }
        return PdfStream.Create(dictionary, BinarySegment.Create(bytes));
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.4.4: Flate data and decoded cap")]
    public void Decode_Flate_ProducesBytesAndCapsBomb()
    {
        // Arrange
        var bytes = new byte[10000];
        var stream = Stream("FlateDecode", PdfFixtureBuilder.Compress(bytes));
        // Act / Assert
        stream.Decode().ToArray().ShouldBe(bytes);
        Should.Throw<ContentFormatException>(() => stream.Decode(new() { MaxDecodedBytes = 100 })).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.4.2 and §7.4.3: ASCII structural filters")]
    [InlineData("ASCIIHexDecode", "48 65 6c6c6f>", "Hello")]
    [InlineData("ASCIIHexDecode", "A>", "\u00a0")]
    [InlineData("ASCII85Decode", "87cURDZ~>", "Hello")]
    [InlineData("ASCII85Decode", "z~>", "\0\0\0\0")]
    public void Decode_AsciiFilters_ProducesExpectedBytes(string filter, string encoded, string expected)
    {
        // Arrange / Act / Assert
        Stream(filter, Encoding.ASCII.GetBytes(encoded)).Decode().ToArray().ShouldBe(Encoding.Latin1.GetBytes(expected));
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.4: Invalid filter data is classified")]
    [InlineData("ASCIIHexDecode", "AG>", ContentFormatErrorCode.InvalidValue)]
    [InlineData("ASCIIHexDecode", "AA", ContentFormatErrorCode.Truncated)]
    [InlineData("ASCII85Decode", "!~>", ContentFormatErrorCode.InvalidValue)]
    [InlineData("ASCII85Decode", "uuuuu~>", ContentFormatErrorCode.InvalidValue)]
    [InlineData("ASCII85Decode", "!z~>", ContentFormatErrorCode.InvalidValue)]
    [InlineData("ASCII85Decode", "87cURDZ", ContentFormatErrorCode.Truncated)]
    public void Decode_MalformedFilters_ReportsCode(string filter, string encoded, ContentFormatErrorCode code)
    {
        // Arrange / Act / Assert
        Should.Throw<ContentFormatException>(() => Stream(filter, Encoding.ASCII.GetBytes(encoded)).Decode()).ErrorCode.ShouldBe(code);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.4.5: RunLength literal and repeated runs")]
    public void Decode_RunLength_HandlesBothRunForms()
    {
        // Arrange
        var stream = Stream("RunLengthDecode", new byte[] { 1, 65, 66, 254, 67, 128 });
        // Act / Assert
        stream.Decode().ToArray().ShouldBe("ABCCC"u8.ToArray());
        Should.Throw<ContentFormatException>(() => Stream("RunLengthDecode", new byte[] { 1, 65 }).Decode()).ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.4.4: LZW clear and end-of-data codes")]
    public void Decode_LzwCodes_ProducesExpectedBytes()
    {
        // Arrange: 9-bit MSB-first codes 256, 65, 66, 257.
        var stream = Stream("LZWDecode", new byte[] { 128, 16, 72, 80, 16 });
        // Act / Assert
        stream.Decode().ToArray().ShouldBe("AB"u8.ToArray());
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.4.4.4: PNG and TIFF predictors")]
    [InlineData(12, new byte[] { 2, 10, 20, 30, 2, 1, 2, 3 }, new byte[] { 10, 20, 30, 11, 22, 33 })]
    [InlineData(2, new byte[] { 10, 10, 10, 11, 11, 11 }, new byte[] { 10, 20, 30, 11, 22, 33 })]
    public void Decode_Predictor_ReconstructsRows(int predictor, byte[] encoded, byte[] expected)
    {
        // Arrange
        var parameters = new PdfDictionary { [PdfName.Create("Predictor"u8)] = PdfInteger.Create(predictor), [PdfName.Create("Columns"u8)] = PdfInteger.Create(3) };
        // Act / Assert
        Stream("FlateDecode", PdfFixtureBuilder.Compress(encoded), parameters).Decode().ToArray().ShouldBe(expected);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.4: Image codecs remain encoded and reject decoding")]
    [InlineData("DCTDecode")]
    [InlineData("JPXDecode")]
    [InlineData("JBIG2Decode")]
    [InlineData("CCITTFaxDecode")]
    [InlineData("Crypt")]
    public void Decode_ImageCodec_RetainsDataAndReportsUnsupported(string filter)
    {
        // Arrange
        var stream = Stream(filter, new byte[] { 1, 2, 3 });
        // Act / Assert
        stream.Data.ToArray(3).ShouldBe(new byte[] { 1, 2, 3 });
        Should.Throw<ContentFormatException>(() => stream.Decode()).ErrorCode.ShouldBe(ContentFormatErrorCode.Unsupported);
    }
}
