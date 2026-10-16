using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Pdf.Tests;

public sealed class PdfCrossReferenceTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.4 and §7.7.3: Classic table resolves all objects and pages")]
    public void Read_ClassicTable_ResolvesObjectsAndPages()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.Classic());
        IContentReader<PdfDocument> reader = new PdfReader();
        // Act
        using var document = reader.Read(source);
        // Assert
        document.Version.ShouldBe(new Version(1, 7));
        document.CrossReferences.Count.ShouldBe(4);
        document.CrossReferences[0].Kind.ShouldBe(PdfCrossReferenceKind.Free);
        document.CrossReferences[0].Identifier.Generation.ShouldBe(65535);
        foreach (var entry in document.CrossReferences.Values)
        {
            if (entry.Kind != PdfCrossReferenceKind.Free) { document.Resolve(entry.Identifier).ShouldNotBeOfType<PdfNull>(); }
        }
        document.PageCount.ShouldBe(1);
        document.Pages[0].CropBox.Count.ShouldBe(4);
        document.Resolve(PdfObjectIdentifier.Create(1)).ShouldBeSameAs(document.Catalog);
        document.Resolve(PdfObjectIdentifier.Create(1, 1)).ShouldBeOfType<PdfNull>();
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.2: Header may occur within the first 1024 bytes")]
    public void Read_PrefixedHeader_UsesCorrectOffsets()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.Classic(prefix: new string('x', 1023)));
        // Act / Assert
        using var document = new PdfReader().Read(source);
        document.PageCount.ShouldBe(1);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5: Broken file structure fails in strict mode")]
    [InlineData("%PDF-1.7", "%BAD-1.7")]
    [InlineData("startxref", "startxxxx")]
    [InlineData("%%EOF", "%%BAD")]
    [InlineData("/Root 1 0 R", "/Root 9 0 R")]
    [InlineData("65535 f", "00000 n")]
    public void Read_MalformedFile_ReportsInvalidStructure(string original, string replacement)
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.Replace(PdfFixtureBuilder.Classic(), original, replacement));
        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => new PdfReader().Read(source));
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        error.Position.ShouldNotBeNull();
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.6: Prev cycles are rejected")]
    public void Read_PrevCycle_RejectsRevisitedOffset()
    {
        // Arrange
        var bytes = PdfFixtureBuilder.Classic(trailer: "/Prev 0000000000");
        bytes = PdfFixtureBuilder.Replace(bytes, "/Prev 0000000000", "/Prev " + PdfFixtureBuilder.StartXref(bytes).ToString("D10", CultureInfo.InvariantCulture));
        using var source = new MemoryStream(bytes);
        // Act / Assert
        Should.Throw<ContentFormatException>(() => new PdfReader().Read(source)).ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.4: Offset must identify the requested object")]
    public void Resolve_WrongObjectOffset_RejectsIdentityMismatch()
    {
        // Arrange
        var bytes = PdfFixtureBuilder.Classic();
        var text = Encoding.ASCII.GetString(bytes);
        var oldOffset = text.IndexOf("1 0 obj", StringComparison.Ordinal).ToString("D10", CultureInfo.InvariantCulture);
        var badOffset = text.IndexOf("2 0 obj", StringComparison.Ordinal).ToString("D10", CultureInfo.InvariantCulture);
        using var source = new MemoryStream(PdfFixtureBuilder.Replace(bytes, oldOffset + " 00000 n", badOffset + " 00000 n"));
        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => new PdfReader().Read(source));
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        error.Position.ShouldBe(long.Parse(badOffset, CultureInfo.InvariantCulture));
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5: Lenient xref rebuilding is explicit")]
    public void Read_BrokenXref_LenientRebuildsObjectHeaders()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.Replace(PdfFixtureBuilder.Classic(), "startxref", "startxxxx"));
        // Act
        using var document = PdfReader.Create(new() { IsLenient = true }).Read(source);
        // Assert
        document.StartXref.ShouldBe(-1);
        document.PageCount.ShouldBe(1);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5: Source ownership is explicit and cancellation is observed")]
    public async Task ReadAsync_BorrowedSource_LeavesOpenAndObservesCancellation()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.Classic());
        // Act
        using (var document = await new PdfReader().ReadAsync(source, CancellationToken.None)) { document.PageCount.ShouldBe(1); }
        // Assert
        source.CanRead.ShouldBeTrue();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await new PdfReader().ReadAsync(source, canceled.Token));
        source.Position = 0;
        PdfReader.Create(new(), leaveOpen: false).Read(source).Dispose();
        source.CanRead.ShouldBeFalse();
    }
}
