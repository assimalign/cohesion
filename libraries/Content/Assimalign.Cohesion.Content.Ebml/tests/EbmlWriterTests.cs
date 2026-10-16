using System;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Ebml.Tests.TestObjects;

namespace Assimalign.Cohesion.Content.Ebml.Tests;

public class EbmlWriterTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - WriteElement: precomputes master size on non-seekable output")]
    public void WriteElement_ForwardOnlyOutput_RoundTrips()
    {
        // Arrange
        using var document = EbmlTestData.Document();
        using var bytes = new MemoryStream();
        using var output = new ForwardOnlyStream(bytes);

        // Act
        document.Save(output);
        bytes.Position = 0;
        using var loaded = EbmlDocument.Load(bytes, document.Schema);

        // Assert
        loaded.Body.Elements[0].Children.Count.ShouldBe(8);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - EndMaster: patches reserved size and restores cursor")]
    public void EndMaster_BackPatch_WritesKnownSize()
    {
        // Arrange
        using var bytes = new MemoryStream();
        using var writer = EbmlWriter.Create(bytes, EbmlTestData.Schema());

        // Act
        writer.BeginMaster(EbmlTestData.Id(0xA0), sizeLength: 2, backPatch: true);
        writer.WriteUnsignedInteger(EbmlTestData.Id(0xA1), 7);
        writer.EndMaster();

        // Assert
        bytes.ToArray().ShouldBe(new byte[] { 0xA0, 0x40, 3, 0xA1, 0x81, 7 });
        bytes.Position.ShouldBe(6);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - WriteValue: explicit widths and Void padding are honored")]
    public void WriteValue_ExplicitWidths_EmitsPadding()
    {
        // Arrange / Act
        var bytes = EbmlTestData.Encode(writer =>
        {
            writer.WriteAsciiString(EbmlTestData.Id(0xA4), "a", length: 3, sizeLength: 2);
            writer.WriteVoid(2);
        });

        // Assert
        bytes.ShouldBe(new byte[] { 0xA4, 0x40, 3, 0x61, 0, 0, 0xEC, 0x82, 0, 0 });
    }

    [Fact(DisplayName = "Cohesion Test [Content.Ebml] - EndMaster: rejects declared size mismatch")]
    public void EndMaster_SizeMismatch_Throws()
    {
        // Arrange
        using var writer = EbmlWriter.Create(new MemoryStream(), EbmlTestData.Schema());
        writer.BeginMaster(EbmlTestData.Id(0xA0), size: 1);

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => writer.EndMaster());
    }
}
