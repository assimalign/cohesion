using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Pdf.Tests;

public sealed class PdfStructureBoundaryTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.8.3: Zero-width fields use type one and generation zero")]
    public void Read_ZeroWidthXrefFields_AppliesDefaults()
    {
        // Arrange
        var initial = PdfFixtureBuilder.Classic();
        var text = Encoding.ASCII.GetString(initial);
        using var source = new MemoryStream(); source.Write(initial);
        var xref = source.Position;
        var bytes = new byte[16];
        for (var i = 0; i < 4; i++)
        {
            var offset = i < 3 ? text.IndexOf($"{i + 1} 0 obj", StringComparison.Ordinal) : xref;
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), (uint)offset);
        }
        PdfFixtureBuilder.Write(source, $"7 0 obj\n<< /Type /XRef /Size 8 /Root 1 0 R /Prev {PdfFixtureBuilder.StartXref(initial)} /W [0 4 0] /Index [1 3 7 1] /Length 16 >>\nstream\n");
        source.Write(bytes);
        PdfFixtureBuilder.Write(source, $"\nendstream\nendobj\nstartxref\n{xref}\n%%EOF\n"); source.Position = 0;
        // Act
        using var document = new PdfReader().Read(source);
        // Assert
        document.PageCount.ShouldBe(1);
        document.CrossReferences[7].Kind.ShouldBe(PdfCrossReferenceKind.Uncompressed);
        document.CrossReferences[7].Identifier.Generation.ShouldBe(0);
        document.Resolve(PdfObjectIdentifier.Create(7)).ShouldBeOfType<PdfStream>();
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5: Stream current position is the PDF origin")]
    public void Read_EmbeddedPdf_UsesRelativeXrefAndAbsoluteErrors()
    {
        // Arrange
        using var source = new MemoryStream(); source.Write(new byte[25]); source.Write(PdfFixtureBuilder.Classic()); source.Position = 25;
        // Act
        using var document = new PdfReader().Read(source);
        // Assert
        document.SourceOffset.ShouldBe(25);
        document.PageCount.ShouldBe(1);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.2 and §7.7.2: Catalog may upgrade the header version")]
    public void Read_CatalogVersion_UpgradesEffectiveVersion()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.Classic(new[] { "<< /Type /Catalog /Pages 2 0 R /Version /2.0 >>", PdfFixtureBuilder.BasicObjects[1], PdfFixtureBuilder.BasicObjects[2] }));
        // Act / Assert
        using var document = new PdfReader().Read(source);
        document.Version.ShouldBe(new Version(2, 0));
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.5: Backward trailer scanning is bounded")]
    public void Read_StartXrefOutsideWindow_ReportsInvalidStructure()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.Classic());
        // Act / Assert
        Should.Throw<ContentFormatException>(() => PdfReader.Create(new() { StartXrefSearchBytes = 8 }).Read(source)).ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.4: Lenient mode repairs offsets naming the wrong object")]
    public void Read_WrongOffsetInLenientMode_RebuildsAndCachesCorrectObject()
    {
        // Arrange
        var bytes = PdfFixtureBuilder.Classic(); var text = Encoding.ASCII.GetString(bytes);
        bytes = PdfFixtureBuilder.Replace(bytes, text.IndexOf("1 0 obj", StringComparison.Ordinal).ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + " 00000 n",
            text.IndexOf("2 0 obj", StringComparison.Ordinal).ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + " 00000 n");
        using var source = new MemoryStream(bytes);
        // Act / Assert
        using var document = PdfReader.Create(new() { IsLenient = true }).Read(source);
        document.StartXref.ShouldBe(-1);
        document.PageCount.ShouldBe(1);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.2–§7.5: Deterministic byte mutations never leak raw parsing exceptions")]
    public void Read_MutatedCorpus_UsesOnlyClassifiedFormatFailures()
    {
        // Arrange
        var random = new Random(473);
        var originals = new[] { PdfFixtureBuilder.Classic(), PdfFixtureBuilder.XrefStream(objectStream: true), PdfFixtureBuilder.Incremental(), PdfFixtureBuilder.InheritedPages() };
        var reader = PdfReader.Create(new() { MaxDecodedBytes = 262144, MaxBufferedBytes = 262144, StartXrefSearchBytes = 262144, MaxObjects = 1024, MaxDepth = 32 });
        var failures = 0;
        // Act / Assert
        foreach (var original in originals)
        {
            for (var iteration = 0; iteration < 256; iteration++)
            {
                var bytes = (byte[])original.Clone(); bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                using var source = new MemoryStream(bytes);
                try
                {
                    using var document = reader.Read(source);
                    foreach (var entry in document.CrossReferences.Values)
                    {
                        if (entry.Kind != PdfCrossReferenceKind.Free) { document.Resolve(entry.Identifier); }
                    }
                    _ = document.PageCount;
                }
                catch (ContentFormatException error)
                {
                    failures++;
                    error.ErrorCode.ShouldNotBe(ContentFormatErrorCode.Unspecified);
                    error.Position.ShouldNotBeNull();
                    error.Position!.Value.ShouldBeGreaterThanOrEqualTo(0);
                }
            }
        }
        failures.ShouldBeGreaterThan(500);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.8 and §7.6: Encrypted xrefs remain readable for structural loading")]
    public void Read_EncryptedXrefStream_LoadsTrailerAndRejectsObjectStreamAccess()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.XrefStream(objectStream: true, extra: "/Encrypt 6 0 R"));
        // Act
        using var document = new PdfReader().Read(source);
        // Assert
        document.IsEncrypted.ShouldBeTrue();
        document.CrossReferences.Count.ShouldBe(7);
        Should.Throw<ContentFormatException>(() => document.PageCount).ErrorCode.ShouldBe(ContentFormatErrorCode.Unsupported);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.4: Subsection counts cannot allocate beyond the xref cap")]
    public void Read_XrefCountBomb_ReportsLimitExceeded()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.Replace(PdfFixtureBuilder.Classic(), "xref\n0 4", "xref\n0 2147483647"));
        // Act / Assert
        Should.Throw<ContentFormatException>(() => new PdfReader().Read(source)).ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.2: Header search never passes byte 1023")]
    public void Read_HeaderOutsideWindow_ReportsInvalidStructure()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.Classic(prefix: new string('x', 1024)));
        // Act / Assert
        Should.Throw<ContentFormatException>(() => PdfReader.Create(new() { IsLenient = true }).Read(source)).ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }

}
