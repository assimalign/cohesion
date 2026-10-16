using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Binary.Tests;
using Assimalign.Cohesion.Content.Ebml.Tests.TestObjects;

namespace Assimalign.Cohesion.Content.Ebml.Tests;

public class EbmlMalformedElementTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §11.3: derived globals do not extend the EBML header")]
    public void Load_DerivedGlobalInHeader_ReportsStructure()
    {
        // Arrange
        var schema = new EbmlSchemaBuilder().Include(EbmlTestData.Schema())
            .Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xB2), "DerivedGlobal", EbmlElementType.Binary, global: true)).Build();
        var bytes = Rfc8794Corpus.HeaderDocument(Rfc8794Corpus.Join(
            Rfc8794Corpus.Element(0x4282, "test"u8.ToArray()), Rfc8794Corpus.Element(0xB2, [])));
        using var source = new MemoryStream(bytes);

        // Act / Assert
        Should.Throw<ContentFormatException>(() => EbmlDocument.Load(source, schema)).ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§4–5: zero marker is malformed, not an indexing error")]
    public void ReadNext_ZeroMarker_ReportsStructure()
    {
        // Arrange
        using var reader = EbmlReader.Create(new NonSeekableStream(new MemoryStream(new byte[] { 0 })));

        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => reader.ReadNext());
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        error.Position.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §6.1: oversized declarations never allocate")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadBinary_OversizedDeclaration_ReportsTruncationOrLimit(bool nonSeekable)
    {
        // Arrange
        using var bytes = new MemoryStream();
        VInt.FromEncoded(0x81).Write(bytes);
        VInt.EncodeSize(1UL << 50).Write(bytes);
        bytes.Position = 0;
        using Stream source = nonSeekable ? new NonSeekableStream(bytes, leaveOpen: true) : bytes;
        using var reader = EbmlReader.Create(source, options: new EbmlReaderOptions { MaxBufferedValueLength = 32 });

        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => { reader.ReadNext(); reader.ReadBinary(); });
        error.ErrorCode.ShouldBe(nonSeekable ? ContentFormatErrorCode.LimitExceeded : ContentFormatErrorCode.Truncated);
        error.Position.ShouldBe(bytes.Length);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §7.7: children cannot cross known parent boundaries")]
    public void ReadNext_ChildExceedsParent_ReportsStructure()
    {
        // Arrange
        using var reader = EbmlReader.Create(new MemoryStream(new byte[] { 0xA0, 0x82, 0xA1, 0x9F }), EbmlTestData.Schema());
        reader.ReadNext().ShouldBeTrue();
        reader.EnterMaster();

        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => reader.ReadNext());
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        error.Position.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§4,7.7: partial child headers are truncated at the parent edge")]
    public void ReadNext_ChildHeaderCrossesParent_ReportsTruncation()
    {
        // Arrange
        using var reader = EbmlReader.Create(new MemoryStream(new byte[] { 0xA0, 0x81, 0xA1 }), EbmlTestData.Schema());
        reader.ReadNext().ShouldBeTrue();
        reader.EnterMaster();

        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => reader.ReadNext());
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
        error.Position.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§7.7,11.1.6.11: depth bombs stop before recursive descent")]
    public void Skip_RecursiveDepthBomb_ReportsLimit()
    {
        // Arrange
        var schema = new EbmlSchemaBuilder().Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x81), "Node",
            EbmlElementType.MasterElement, recursive: true)).Build();
        var bytes = Rfc8794Corpus.Element(0x81, []);
        for (var i = 1; i < 12; i++)
        {
            bytes = Rfc8794Corpus.Element(0x81, bytes);
        }
        using var reader = EbmlReader.Create(new NonSeekableStream(new MemoryStream(bytes)), schema,
            new EbmlReaderOptions { MaxDepth = 4 });
        reader.ReadNext().ShouldBeTrue();

        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => reader.Skip());
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        error.Position.ShouldBe(8);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§6,7.7: document node limits bound zero-length floods")]
    public void Load_TooManyEmptyElements_ReportsLimit()
    {
        // Arrange
        using var document = EbmlTestData.Document();
        using var source = new MemoryStream();
        document.Save(source);
        source.Position = 0;

        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => EbmlDocument.Load(source, document.Schema,
            new EbmlReaderOptions { MaxElementCount = 3 }));
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §11: lenient diagnostics are bounded")]
    public void ReadNext_DiagnosticFlood_ReportsLimit()
    {
        // Arrange — repeated root-only children are invalid at this level.
        byte[] bytes = [0xA1, 0x80, 0xA1, 0x80, 0xA1, 0x80];
        using var reader = EbmlReader.Create(new MemoryStream(bytes), EbmlTestData.Schema(),
            new EbmlReaderOptions { Lenient = true, MaxDiagnostics = 2 });

        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() =>
        {
            while (reader.ReadNext())
            {
                reader.Skip();
            }
        });
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        reader.Diagnostics.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§4–5: nested slices retain absolute error positions")]
    public void ReadNext_NestedSlice_ReportsSourcePosition()
    {
        // Arrange
        var bytes = new byte[22];
        bytes[20] = 0x1A;
        bytes[21] = 0x45;
        using var source = new MemoryStream(bytes);
        using var outer = ContentSliceStream.Create(source, 10, 12);
        using var inner = ContentSliceStream.Create(outer, 10, 2);
        using var reader = EbmlReader.Create(inner);

        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => reader.ReadNext());
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
        error.Position.ShouldBe(22);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §11.2.5: body size widths obey the parsed header")]
    public void Load_OverwideBodySize_ReportsLimit()
    {
        // Arrange
        var bytes = Rfc8794Corpus.HeaderDocument(Rfc8794Corpus.Join(
            Rfc8794Corpus.Element(0x4282, "test"u8.ToArray()), Rfc8794Corpus.Element(0x42F3, [1])),
            Rfc8794Corpus.Element(0xA0, [], 2));
        using var source = new MemoryStream(bytes);

        // Act / Assert
        Should.Throw<ContentFormatException>(() => EbmlDocument.Load(source, EbmlTestData.Schema()))
            .ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §11.2.5: saving refuses header/body width disagreement")]
    public void Save_OverwideBodySize_ReportsLimitBeforeOutput()
    {
        // Arrange
        using var document = EbmlTestData.Document();
        document.Header.EBMLMaxSizeLength = 1;
        using var output = new MemoryStream();

        // Act / Assert — the preserved unknown element deliberately uses a three-octet size.
        Should.Throw<ContentFormatException>(() => document.Save(output)).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        output.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§7.3,11: lenient malformed leaves survive byte-for-byte")]
    public void Load_LenientInvalidFloat_PreservesWireBytes()
    {
        // Arrange
        var bytes = Rfc8794Corpus.HeaderDocument(Rfc8794Corpus.Element(0x4282, "test"u8.ToArray()),
            Rfc8794Corpus.Element(0xA0, Rfc8794Corpus.Element(0xA3, [1, 2, 3])));
        using var source = new MemoryStream(bytes);

        // Act
        using var document = EbmlDocument.Load(source, EbmlTestData.Schema(), new EbmlReaderOptions { Lenient = true });
        using var output = new MemoryStream();
        document.Save(output);

        // Assert
        document.Diagnostics.Count.ShouldBe(1);
        document.Body.Elements[0].Children[0].Type.ShouldBe(EbmlElementType.Binary);
        output.ToArray().ShouldBe(bytes);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §7.8: skipping a non-seekable payload does not buffer it")]
    public void Skip_AboveBufferLimit_DrainsWithoutAllocation()
    {
        // Arrange
        var bytes = Rfc8794Corpus.Element(0x81, new byte[1024]);
        using var reader = EbmlReader.Create(new NonSeekableStream(new MemoryStream(bytes)),
            options: new EbmlReaderOptions { MaxBufferedValueLength = 0 });
        reader.ReadNext().ShouldBeTrue();

        // Act / Assert
        reader.Skip();
        reader.Position.ShouldBe(bytes.Length);
        reader.ReadNext().ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§4–7: synthesized hostile bytes never leak parsing exceptions")]
    public void ReadNext_DeterministicHostileInputs_ReportsOnlyFormatFailures()
    {
        // Arrange
        var random = new Random(8794);

        // Act / Assert
        for (var sample = 0; sample < 512; sample++)
        {
            var bytes = new byte[random.Next(1, 65)];
            random.NextBytes(bytes);
            using var reader = EbmlReader.Create(new NonSeekableStream(new MemoryStream(bytes)),
                options: new EbmlReaderOptions { MaxBufferedValueLength = 64, MaxDepth = 4 });
            try
            {
                while (reader.ReadNext())
                {
                    reader.Skip();
                }
            }
            catch (ContentFormatException error)
            {
                error.ErrorCode.ShouldNotBe(ContentFormatErrorCode.Unspecified);
                error.Position.ShouldNotBeNull();
                error.Position!.Value.ShouldBeInRange(0, bytes.Length);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §11.3.1: cached nested CRC computation avoids exponential source reads")]
    public async Task WriteAsync_NestedCrcMasters_KeepsCopyPassesBounded()
    {
        // Arrange
        var id = VInt.FromEncoded(0x81);
        var schema = new EbmlSchemaBuilder().Include(EbmlSchema.Header)
            .Add(EbmlElementDefinition.Create(id, "Node", EbmlElementType.MasterElement, recursive: true)).Build();
        using var document = EbmlDocument.Create(schema);
        var root = EbmlElement.Create(id, EbmlElementType.MasterElement);
        document.Body.Elements.Add(root);
        var current = root;
        for (var depth = 0; depth < 12; depth++)
        {
            current.Children.Add(EbmlElement.Create(VInt.FromEncoded(0xBF), EbmlElementType.Binary, BinarySegment.Create(new byte[4])));
            var child = EbmlElement.Create(id, EbmlElementType.MasterElement);
            current.Children.Add(child);
            current = child;
        }
        using var payload = new SparsePayloadStream(new byte[8], 0);
        current.Children.Add(EbmlElement.Create(VInt.FromEncoded(0x82), EbmlElementType.Binary, BinarySegment.Create(payload, 0, 8)));
        using var output = new MemoryStream();

        // Act
        await EbmlDocument.CreateWriter().WriteAsync(output, document, CancellationToken.None);

        // Assert
        payload.BytesRead.ShouldBeLessThanOrEqualTo(8L * 13);
        output.Position = 0;
        using var loaded = EbmlDocument.Load(output, schema, new EbmlReaderOptions { VerifyCrc32 = true });
        loaded.Diagnostics.ShouldBeEmpty();
    }
}
