using System;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Pdf.Tests;

public sealed class PdfFilterBoundaryTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.4.4: LZW width transitions, full dictionary and clear resets")]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    public void Decode_LzwBoundaries_ReconstructsLiteralSequence(int earlyChange, int resets)
    {
        // Arrange
        var encoded = PdfFixtureBuilder.LzwLiterals(5000, earlyChange, resets);
        var expected = new byte[5000 * (resets + 1)];
        for (var i = 0; i < expected.Length; i++) { expected[i] = (byte)((i % 5000) & 255); }
        var parameters = new PdfDictionary { [PdfName.Create("EarlyChange"u8)] = PdfInteger.Create(earlyChange) };
        // Act / Assert
        PdfFilterTests.Stream("LZWDecode", encoded, parameters).Decode().ToArray().ShouldBe(expected);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.4.4: LZW next-code special case and malformed code")]
    public void Decode_LzwNextCode_ExpandsRepeatedSequence()
    {
        // Arrange / Act / Assert
        PdfFilterTests.Stream("LZWDecode", PdfFixtureBuilder.LzwNineBitCodes(256, 65, 258, 259, 257)).Decode().ToArray().ShouldBe("AAAAAA"u8.ToArray());
        Should.Throw<ContentFormatException>(() => PdfFilterTests.Stream("LZWDecode", PdfFixtureBuilder.LzwNineBitCodes(256, 258, 257)).Decode()).ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue);
        Should.Throw<ContentFormatException>(() => PdfFilterTests.Stream("LZWDecode", PdfFixtureBuilder.LzwNineBitCodes(256, 65)).Decode()).ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.4.4.4: Every PNG row predictor")]
    [InlineData(0, new byte[] { 10, 20, 30, 15, 25, 35 })]
    [InlineData(1, new byte[] { 10, 10, 10, 15, 10, 10 })]
    [InlineData(2, new byte[] { 10, 20, 30, 5, 5, 5 })]
    [InlineData(3, new byte[] { 10, 15, 20, 10, 8, 8 })]
    [InlineData(4, new byte[] { 10, 10, 10, 5, 5, 5 })]
    public void Decode_PngFilter_ReversesRowPrediction(int filter, byte[] differences)
    {
        // Arrange
        var encoded = new byte[] { (byte)filter, differences[0], differences[1], differences[2], (byte)filter, differences[3], differences[4], differences[5] };
        var parameters = new PdfDictionary { [PdfName.Create("Predictor"u8)] = PdfInteger.Create(15), [PdfName.Create("Columns"u8)] = PdfInteger.Create(3) };
        // Act / Assert
        PdfFilterTests.Stream("FlateDecode", PdfFixtureBuilder.Compress(encoded), parameters).Decode().ToArray().ShouldBe(new byte[] { 10, 20, 30, 15, 25, 35 });
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.4.4.4: TIFF packed components and 16-bit carry")]
    [InlineData(1, 4, new byte[] { 208 }, new byte[] { 144 })] // deltas 1,1,0,1 -> samples 1,0,0,1
    [InlineData(2, 4, new byte[] { 85 }, new byte[] { 108 })] // deltas 1,1,1,1 -> samples 1,2,3,0
    [InlineData(4, 4, new byte[] { 17, 17 }, new byte[] { 18, 52 })]
    [InlineData(8, 3, new byte[] { 250, 10, 1 }, new byte[] { 250, 4, 5 })]
    [InlineData(16, 3, new byte[] { 0, 255, 0, 2, 255, 255 }, new byte[] { 0, 255, 1, 1, 1, 0 })]
    public void Decode_TiffBitWidths_ReconstructsSamples(int bits, int columns, byte[] deltas, byte[] expected)
    {
        // Arrange
        var parameters = new PdfDictionary
        {
            [PdfName.Create("Predictor"u8)] = PdfInteger.Create(2), [PdfName.Create("Columns"u8)] = PdfInteger.Create(columns),
            [PdfName.Create("BitsPerComponent"u8)] = PdfInteger.Create(bits)
        };
        // Act / Assert
        PdfFilterTests.Stream("FlateDecode", PdfFixtureBuilder.Compress(deltas), parameters).Decode().ToArray().ShouldBe(expected);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.4.4.4: Predictor geometry cannot cause unchecked allocation")]
    [InlineData("Columns", 2147483647, ContentFormatErrorCode.LimitExceeded)]
    [InlineData("Colors", 2147483647, ContentFormatErrorCode.LimitExceeded)]
    [InlineData("BitsPerComponent", 3, ContentFormatErrorCode.InvalidValue)]
    [InlineData("Columns", 0, ContentFormatErrorCode.InvalidValue)]
    public void Decode_PredictorGeometry_RejectsInvalidOrOversizeParameters(string key, int value, ContentFormatErrorCode code)
    {
        // Arrange
        var parameters = new PdfDictionary { [PdfName.Create("Predictor"u8)] = PdfInteger.Create(12), [PdfName.Create(Encoding.ASCII.GetBytes(key))] = PdfInteger.Create(value) };
        // Act / Assert
        Should.Throw<ContentFormatException>(() => PdfFilterTests.Stream("FlateDecode", PdfFixtureBuilder.Compress(new byte[] { 2, 1 }), parameters).Decode()).ErrorCode.ShouldBe(code);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.4.1: Filter arrays decode in declared order")]
    public void Decode_FilterChain_AppliesFiltersAndAlignedParameters()
    {
        // Arrange
        var filters = new PdfArray(); filters.Add(PdfName.Create("ASCIIHexDecode"u8)); filters.Add(PdfName.Create("FlateDecode"u8));
        var parameters = new PdfArray(); parameters.Add(new PdfNull()); parameters.Add(new PdfNull());
        var dictionary = new PdfDictionary { [PdfName.Create("Filter"u8)] = filters, [PdfName.Create("DecodeParms"u8)] = parameters };
        var compressed = PdfFixtureBuilder.Compress("chain"u8);
        var stream = PdfStream.Create(dictionary, BinarySegment.Create(Encoding.ASCII.GetBytes(Convert.ToHexString(compressed) + ">")));
        // Act / Assert
        stream.Decode().ToArray().ShouldBe("chain"u8.ToArray());
        parameters.RemoveAt(0);
        Should.Throw<ContentFormatException>(() => stream.Decode()).ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.4: All expanding filters enforce the output cap")]
    [InlineData("ASCIIHexDecode", "414243>")]
    [InlineData("ASCII85Decode", "z~>")]
    public void Decode_AsciiBomb_ReportsLimitExceeded(string filter, string encoded)
    {
        // Arrange / Act / Assert
        Should.Throw<ContentFormatException>(() => PdfFilterTests.Stream(filter, Encoding.ASCII.GetBytes(encoded)).Decode(new() { MaxDecodedBytes = 2 })).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.4.4 and §7.4.5: RunLength and LZW bombs are capped")]
    public void Decode_CompressedBombs_ReportsLimitExceeded()
    {
        // Arrange / Act / Assert
        Should.Throw<ContentFormatException>(() => PdfFilterTests.Stream("RunLengthDecode", new byte[] { 129, 65, 128 }).Decode(new() { MaxDecodedBytes = 4 })).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        Should.Throw<ContentFormatException>(() => PdfFilterTests.Stream("LZWDecode", PdfFixtureBuilder.LzwNineBitCodes(256, 65, 258, 259, 257)).Decode(new() { MaxDecodedBytes = 4 })).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }
}
