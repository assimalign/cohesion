using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Pdf.Tests;

public sealed class PdfFullSaveTests
{
    public static IEnumerable<object[]> Corpus() => PdfWriterAssertions.Corpus();
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.4–§7.5.8 and §7.7.3: Every reader fixture survives all full-save layouts")]
    [MemberData(nameof(Corpus))]
    public void Write_ReaderCorpus_PreservesGraphsAndInheritance(string name, byte[] bytes, int pages)
    {
        // Arrange
        name.ShouldNotBeNullOrEmpty();
        using var source = new MemoryStream(bytes);
        using var document = new PdfReader().Read(source);
        for (var mode = 0; mode < 3; mode++)
        {
            using var output = new MemoryStream();
            var writer = PdfWriter.Create(new() { UseCrossReferenceStream = mode != 0, PackObjects = mode == 2 });
            // Act
            writer.Write(output, document); output.Position = 0;
            using var saved = new PdfReader().Read(output);
            // Assert
            PdfWriterAssertions.Graphs(document, saved); saved.PageCount.ShouldBe(pages);
            saved.Version.ShouldBe(document.Version);
            saved.Trailer.Get("Prev").ShouldBeNull(); saved.Trailer.Get("XRefStm").ShouldBeNull();
            saved.Trailer.Get("ID").ShouldBeOfType<PdfArray>().Count.ShouldBe(2);
        }
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.8 and §7.4.4: Replaced streams are encoded or stored explicitly")]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_ReplacedStream_ReloadsEditedPayload(bool compress)
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.IndirectLength());
        using var document = new PdfReader().Read(source);
        var stream = document.Resolve(PdfObjectIdentifier.Create(4)).ShouldBeOfType<PdfStream>();
        stream.ReplaceData(BinarySegment.Create("new payload"u8.ToArray()));
        using var output = new MemoryStream();
        // Act
        PdfWriter.Create(new() { CompressModifiedStreams = compress }).Write(output, document); output.Position = 0;
        using var saved = new PdfReader().Read(output);
        // Assert
        var written = saved.Resolve(PdfObjectIdentifier.Create(4)).ShouldBeOfType<PdfStream>();
        written.Decode().ToArray().ShouldBe("new payload"u8.ToArray());
        written.Dictionary.Get("Length").ShouldBeOfType<PdfInteger>().Value.ShouldBe(written.Data.Length);
        if (compress) { written.Dictionary.Get("Filter").ShouldBeOfType<PdfName>().ToString().ShouldBe("/FlateDecode"); }
        else { written.Dictionary.Get("Filter").ShouldBeNull(); }
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.5–§7.5.7: Full save retains nested mutations and trailer identifiers")]
    public void Write_InPlaceMutation_PersistsDictionaryArrayAndTrailer()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.Classic(trailer: "/ID [<0011> <2233>]"));
        using var document = new PdfReader().Read(source);
        var pages = document.Resolve(PdfObjectIdentifier.Create(2)).ShouldBeOfType<PdfDictionary>();
        pages.Get("MediaBox").ShouldBeOfType<PdfArray>()[2] = PdfInteger.Create(500);
        pages.Get("Resources").ShouldBeOfType<PdfDictionary>()[PdfName.Create("Edited"u8)] = PdfBoolean.Create(true);
        var info = new PdfDictionary(); info[PdfName.Create("Title"u8)] = PdfString.Create("saved"u8);
        var id = document.Add(info); document.Trailer[PdfName.Create("Info"u8)] = PdfIndirectReference.Create(id);
        using var output = new MemoryStream();
        // Act
        new PdfWriter().Write(output, document); output.Position = 0;
        using var saved = new PdfReader().Read(output);
        // Assert
        PdfWriterAssertions.Graphs(document, saved);
        saved.Trailer.Get("Info").ShouldBeOfType<PdfIndirectReference>().Identifier.ShouldBe(id);
        PdfWriterAssertions.Object(document.Trailer.Get("ID")!, saved.Trailer.Get("ID")!);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5: Writer seam supports a forward-only borrowed destination")]
    public async Task WriteAsync_NonSeekableDestination_EmitsReadablePdf()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.Classic());
        using var document = new PdfReader().Read(source);
        using var storage = new MemoryStream();
        using var output = new Internal.PdfOutput(storage);
        IContentWriter<PdfDocument> writer = new PdfWriter();
        // Act
        await writer.WriteAsync(output, document, CancellationToken.None); storage.Position = 0;
        // Assert
        using var saved = new PdfReader().Read(storage);
        saved.PageCount.ShouldBe(1); storage.CanRead.ShouldBeTrue(); source.CanRead.ShouldBeTrue();
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.7: Packing excludes stream Length identities")]
    public void Write_ObjectStreams_LeavesLengthObjectsUncompressed()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.IndirectLength());
        using var document = new PdfReader().Read(source);
        using var output = new MemoryStream();
        // Act
        PdfWriter.Create(new() { UseCrossReferenceStream = true, PackObjects = true }).Write(output, document); output.Position = 0;
        using var saved = new PdfReader().Read(output);
        // Assert
        saved.CrossReferences[4].Kind.ShouldBe(PdfCrossReferenceKind.Uncompressed);
        saved.CrossReferences[5].Kind.ShouldBe(PdfCrossReferenceKind.Uncompressed);
        saved.CrossReferences[3].Kind.ShouldBe(PdfCrossReferenceKind.Compressed);
    }
}
