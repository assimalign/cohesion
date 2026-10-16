using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Binary.Tests;
using Assimalign.Cohesion.Content.Pdf.Internal;

namespace Assimalign.Cohesion.Content.Pdf.Tests;

public sealed class PdfMalformedCorpusTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.8: Every stream payload prefix uses the shared TruncationCorpus")]
    public void ReadIndirect_TruncatedStreamPrefixes_ReportsTruncated()
    {
        // Arrange
        var payload = "abcdefgh"u8.ToArray();
        // Act / Assert
        TruncationCorpus.AssertPrefixes(payload, prefix =>
        {
            using var source = new MemoryStream();
            PdfFixtureBuilder.Write(source, "1 0 obj\n<< /Length 8 >>\nstream\n");
            source.Write(prefix.Span);
            if (prefix.Length == payload.Length) { PdfFixtureBuilder.Write(source, "\nendstream\nendobj"); }
            source.Position = 0;
            using var input = new PdfInput(source);
            new PdfParser(input, new()).ReadIndirect();
            return true;
        });
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.2: Every incomplete header is InvalidStructure")]
    public void Read_HeaderPrefixes_ReportsInvalidStructure()
    {
        // Arrange
        var header = "%PDF-1.7\n"u8.ToArray();
        // Act / Assert
        for (var length = 0; length < header.Length; length++)
        {
            using var source = new MemoryStream(header, 0, length);
            Should.Throw<ContentFormatException>(() => new PdfReader().Read(source)).ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        }
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.8: Xref stream widths and ranges are validated before allocation")]
    [InlineData("/W [1 4 2]", "/W [9 4 2]", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("/W [1 4 2]", "/W [0 0 0]", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("/W [1 4 2]", "/W [1 4]", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("/Index [0 7]", "/Index [0 8]", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("/Index [0 7]", "/Index [0 4 3 3]", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("/Index [0 7]", "/Index [0]", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("/Index [0 7]", "/Index [1 0 R 7]", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("/Index [0 7]", "/Index [-1 7]", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("/Filter /FlateDecode", "/Filter 1 0 R", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("/Predictor 12", "/Predictor 99", ContentFormatErrorCode.InvalidStructure)]
    public void Read_MalformedXrefStream_ReportsCode(string before, string after, ContentFormatErrorCode code)
    {
        // Arrange: changes occur in the last object's dictionary, leaving all recorded offsets valid.
        using var source = new MemoryStream(PdfFixtureBuilder.Replace(PdfFixtureBuilder.XrefStream(), before, after));
        // Act / Assert
        Should.Throw<ContentFormatException>(() => new PdfReader().Read(source)).ErrorCode.ShouldBe(code);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.7: Object stream headers are checked before extraction")]
    [InlineData("9 0 6 29 ")]
    [InlineData("3 0 3 29 ")]
    [InlineData("3 0 6 0 ")]
    [InlineData("3 0 6 9999999 ")]
    public void Resolve_MalformedObjectStreamHeader_ReportsInvalidStructure(string header)
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.XrefStream(objectStream: true, objectStreamHeader: header));
        using var document = new PdfReader().Read(source);
        // Act / Assert
        Should.Throw<ContentFormatException>(() => document.PageCount).ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.7: Object stream declared counts are bounded")]
    public void Resolve_ObjectStreamCountBomb_ReportsLimitExceeded()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.XrefStream(objectStream: true, objectStreamCount: int.MaxValue));
        using var document = new PdfReader().Read(source);
        // Act / Assert
        Should.Throw<ContentFormatException>(() => document.PageCount).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.8.3: Compressed xref index must match the member identity")]
    public void Resolve_MismatchedCompressedIndex_ReportsInvalidStructure()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.XrefStream(objectStream: true, mutateRows: rows =>
            BinaryPrimitives.WriteUInt16BigEndian(rows.AsSpan(3 * 7 + 5), 1)));
        using var document = new PdfReader().Read(source);
        // Act / Assert
        Should.Throw<ContentFormatException>(() => document.PageCount).ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.8.3: Invalid xref fields cannot overflow model components")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Read_InvalidXrefField_ReportsInvalidStructure(int mode)
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.XrefStream(mutateRows: rows =>
        {
            if (mode == 0) { rows[0] = 1; } // object zero must be free
            if (mode == 1) { BinaryPrimitives.WriteUInt32BigEndian(rows.AsSpan(7 + 1), uint.MaxValue); }
            if (mode == 2) { rows[7] = 2; BinaryPrimitives.WriteUInt32BigEndian(rows.AsSpan(7 + 1), 0); }
        }));
        // Act / Assert
        Should.Throw<ContentFormatException>(() => new PdfReader().Read(source)).ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.6 and §7.5.8: Section and decoded limits bound traversal")]
    public void Read_TraversalLimits_ReportsLimitExceeded()
    {
        // Arrange
        using var chain = new MemoryStream(PdfFixtureBuilder.Incremental());
        using var decoded = new MemoryStream(PdfFixtureBuilder.XrefStream());
        using var hybrid = new MemoryStream(PdfFixtureBuilder.XrefStream(hybrid: true));
        // Act / Assert
        Should.Throw<ContentFormatException>(() => PdfReader.Create(new() { MaxCrossReferenceSections = 1 }).Read(chain)).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        Should.Throw<ContentFormatException>(() => PdfReader.Create(new() { MaxDecodedBytes = 48 }).Read(decoded)).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        Should.Throw<ContentFormatException>(() => PdfReader.Create(new() { MaxCrossReferenceSections = 1 }).Read(hybrid)).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.6: A depth bomb fails with a stable code")]
    public void Resolve_DepthBomb_ReportsLimitExceeded()
    {
        // Arrange
        var objects = new[] { PdfFixtureBuilder.BasicObjects[0], PdfFixtureBuilder.BasicObjects[1], PdfFixtureBuilder.BasicObjects[2], new string('[', 10000) + "0" + new string(']', 10000) };
        using var source = new MemoryStream(PdfFixtureBuilder.Classic(objects));
        using var document = new PdfReader().Read(source);
        // Act / Assert
        Should.Throw<ContentFormatException>(() => document.Resolve(PdfObjectIdentifier.Create(4))).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.7.3: Cyclic page trees and invalid counts are rejected")]
    public void Read_CyclicPageTree_ReportsInvalidStructure()
    {
        // Arrange
        var objects = new[] { PdfFixtureBuilder.BasicObjects[0], "<< /Type /Pages /Kids [2 0 R] /Count 1 /Parent 2 0 R >>" };
        using var source = new MemoryStream(PdfFixtureBuilder.Classic(objects));
        using var document = new PdfReader().Read(source);
        // Act / Assert
        Should.Throw<ContentFormatException>(() => document.PageCount).ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.8: Cyclic indirect Length cannot recurse without a bound")]
    public void Resolve_CyclicLength_ReportsInvalidStructure()
    {
        // Arrange
        var objects = new[] { PdfFixtureBuilder.BasicObjects[0], PdfFixtureBuilder.BasicObjects[1], PdfFixtureBuilder.BasicObjects[2], "<< /Length 4 0 R >>\nstream\nabc\nendstream" };
        using var source = new MemoryStream(PdfFixtureBuilder.Classic(objects));
        using var document = new PdfReader().Read(source);
        // Act / Assert
        Should.Throw<ContentFormatException>(() => document.Resolve(PdfObjectIdentifier.Create(4))).ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5: Recovery is bounded and skips known stream payloads")]
    public void Read_RecoveryScan_RejectsLimitAndIgnoresPayloadHeaders()
    {
        // Arrange
        var payload = "8 0 obj\n<< /Fake true >>\nendobj";
        var objects = new[] { PdfFixtureBuilder.BasicObjects[0], PdfFixtureBuilder.BasicObjects[1], PdfFixtureBuilder.BasicObjects[2], $"<< /Length {payload.Length} >>\nstream\n{payload}\nendstream" };
        var bytes = PdfFixtureBuilder.Replace(PdfFixtureBuilder.Classic(objects), "startxref", "startxxxx");
        using var source = new MemoryStream(bytes);
        // Act / Assert
        Should.Throw<ContentFormatException>(() => PdfReader.Create(new() { IsLenient = true, MaxRecoveryScanBytes = 10 }).Read(source)).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        source.Position = 0;
        using var document = PdfReader.Create(new() { IsLenient = true }).Read(source);
        document.CrossReferences.ContainsKey(8).ShouldBeFalse();
        document.PageCount.ShouldBe(1);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.7: Recovery can reconstruct compressed object entries")]
    public void Read_BrokenObjectStreamXref_RebuildsMemberLocations()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.Replace(PdfFixtureBuilder.XrefStream(objectStream: true), "startxref", "startxxxx"));
        // Act
        using var document = PdfReader.Create(new() { IsLenient = true }).Read(source);
        // Assert
        document.PageCount.ShouldBe(1);
        document.Resolve(PdfObjectIdentifier.Create(6)).ShouldBeOfType<PdfDictionary>();
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5: Random access is required and short reads are supported")]
    public void Read_StreamCapabilities_RejectsNonSeekableAndHandlesShortReads()
    {
        // Arrange
        using var forward = new PdfTestStream(new MemoryStream(PdfFixtureBuilder.Classic()), false);
        using var shortReads = new PdfTestStream(new MemoryStream(PdfFixtureBuilder.Classic()), true);
        // Act / Assert
        Should.Throw<ContentFormatException>(() => new PdfReader().Read(forward)).ErrorCode.ShouldBe(ContentFormatErrorCode.Unsupported);
        using var document = new PdfReader().Read(shortReads);
        document.PageCount.ShouldBe(1);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.8 and §7.5: Forty GiB payload stays lazy")]
    public void Read_LargeSparseSource_SeeksWithoutMaterializingPayload()
    {
        // Arrange
        using var source = new SparsePdfStream();
        // Act
        using var document = new PdfReader().Read(source);
        var stream = document.Resolve(PdfObjectIdentifier.Create(4)).ShouldBeOfType<PdfStream>();
        // Assert
        document.PageCount.ShouldBe(1);
        stream.Data.Length.ShouldBe(SparsePdfStream.PayloadLength);
        stream.Data.IsMaterialized.ShouldBeFalse();
        source.BytesRead.ShouldBeLessThan(2 * 1024 * 1024);
        Should.Throw<ContentFormatException>(() => stream.Decode()).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5: Positions remain absolute inside source slices")]
    public void Read_SlicedSource_ReportsAbsolutePosition()
    {
        // Arrange
        var bytes = PdfFixtureBuilder.Replace(PdfFixtureBuilder.Classic(), "%PDF-1.7", "%BAD-1.7");
        using var parent = new MemoryStream(); parent.Write(new byte[17]); parent.Write(bytes);
        using var slice = ContentSliceStream.Create(parent, 17, bytes.Length);
        // Act / Assert
        Should.Throw<ContentFormatException>(() => new PdfReader().Read(slice)).Position.ShouldBe(17);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.8: Decode limits report absolute positions through nested sources")]
    public void Decode_SlicedPayloadLimit_ReportsAbsolutePosition()
    {
        // Arrange
        var bytes = PdfFixtureBuilder.IndirectLength();
        using var parent = new MemoryStream(); parent.Write(new byte[37]); parent.Write(bytes);
        using var slice = ContentSliceStream.Create(parent, 37, bytes.Length);
        using var document = new PdfReader().Read(slice);
        var stream = document.Resolve(PdfObjectIdentifier.Create(4)).ShouldBeOfType<PdfStream>();
        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => stream.Decode(new() { MaxBufferedBytes = 2 }));
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        error.Position.ShouldBe(37 + Encoding.ASCII.GetString(bytes).IndexOf("48656c6c6f>", StringComparison.Ordinal));
    }

}
