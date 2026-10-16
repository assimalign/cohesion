using System;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml.Tests.TestObjects;

namespace Assimalign.Cohesion.Content.Ebml.Tests;

public class EbmlLayoutTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§6,7: GetEncodedLength: measures trees, retained widths and preserved scalar encodings")]
    public void GetEncodedLength_LoadedAndEditedTree_MatchesWrittenBytes()
    {
        // Arrange — document emitted by this package's own writer.
        using var built = EbmlTestData.Document();
        using var bytes = new MemoryStream();
        built.Save(bytes);
        bytes.Position = 0;
        using var document = EbmlDocument.Load(bytes, built.Schema);
        var root = document.Body.Elements[0];

        // Act / Assert
        foreach (var unknown in new[] { false, true })
        {
            root.IsUnknownSize = unknown;
            root.Children[3].Value = new string('x', 200);
            var measured = EbmlWriter.GetEncodedLength(root, document.Schema);
            using var output = new MemoryStream();
            using var forward = new ForwardOnlyStream(output);
            using var writer = EbmlWriter.Create(forward, document.Schema);
            writer.WriteElement(root);
            writer.Position.ShouldBe(measured);
            output.Length.ShouldBe(measured);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§6,7: GetEncodedLength: explicit scalar widths match streaming writes")]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(8)]
    public void GetEncodedLength_DefaultedValue_MatchesExplicitWidth(int width)
    {
        // Arrange
        var id = VInt.FromEncoded(0x81);
        var schema = new EbmlSchemaBuilder().Add(EbmlElementDefinition.Create(id, "Default",
            EbmlElementType.UnsignedInteger, defaultValue: 7UL)).Build();
        using var output = new MemoryStream();
        using var writer = EbmlWriter.Create(output, schema);

        // Act
        var measured = EbmlWriter.GetEncodedLength(id, EbmlElementType.UnsignedInteger, 7UL, schema, width, 3);
        writer.WriteUnsignedInteger(id, 7, width, 3);

        // Assert
        output.Length.ShouldBe(measured);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§6,7: GetEncodedLength: large binary ranges are measured without payload reads")]
    public void GetEncodedLength_LazyFortyGiBPayload_DoesNotRead()
    {
        // Arrange
        const long size = 40L * 1024 * 1024 * 1024;
        using var source = new SparsePayloadStream(Array.Empty<byte>(), size);
        var segment = BinarySegment.Create(source, 0, size);
        var element = EbmlElement.Create(VInt.FromEncoded(0x81), EbmlElementType.Binary, segment);

        // Act / Assert
        EbmlWriter.GetEncodedLength(element).ShouldBe(size + 7);
        EbmlWriter.GetEncodedLength(element.Identifier, element.Type, segment).ShouldBe(size + 7);
        source.BytesRead.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §6: WriteRawElement: copies exact original bytes to non-seekable output")]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteRawElement_OriginalElement_PreservesBytesAndCounts(bool unknown)
    {
        // Arrange — an intentionally wide original header, emitted by EBML.
        var bytes = EbmlTestData.Encode(writer =>
        {
            writer.BeginMaster(VInt.FromEncoded(0xA0), unknown ? null : 3, 4);
            writer.WriteUnsignedInteger(VInt.FromEncoded(0xA1), 7);
            writer.EndMaster();
        });
        using var source = new MemoryStream(bytes);
        var segment = BinarySegment.Create(source, 0, bytes.Length);
        using var output = new MemoryStream();
        using var forward = new ForwardOnlyStream(output);
        using var writer = EbmlWriter.Create(forward, EbmlTestData.Schema());

        // Act
        writer.WriteRawElement(segment);

        // Assert
        writer.Position.ShouldBe(bytes.Length);
        output.ToArray().ShouldBe(bytes);
        source.CanRead.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §§5,6: WriteRawElement: malformed outer headers fail before output")]
    [InlineData(new byte[] { 0x80, 0x80 })]
    [InlineData(new byte[] { 0x81, 0x82, 1 })]
    [InlineData(new byte[] { 0x81, 0x80, 1 })]
    [InlineData(new byte[] { 0x81, 0xFF })]
    public void WriteRawElement_InvalidRange_Rejects(byte[] bytes)
    {
        // Arrange — hand-built malformed outer headers.
        using var output = new MemoryStream();
        using var writer = EbmlWriter.Create(output);

        // Act / Assert
        Should.Throw<ContentFormatException>(() => writer.WriteRawElement(BinarySegment.Create(bytes)));
        output.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - RFC 8794 §6: WriteRawElement: respects known master bounds")]
    public void WriteRawElement_ExceedsParent_RejectsBeforeCopy()
    {
        // Arrange
        using var output = new MemoryStream();
        using var writer = EbmlWriter.Create(output, EbmlTestData.Schema());
        writer.BeginMaster(VInt.FromEncoded(0xA0), 2);
        var start = writer.Position;

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => writer.WriteRawElement(BinarySegment.Create(new byte[] { 0x81, 0x81, 7 })));
        writer.Position.ShouldBe(start);
        output.Length.ShouldBe(start);
    }
}
