using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Pdf.Tests;

public sealed class PdfIncrementalSaveTests
{
    public static IEnumerable<object[]> Corpus() => PdfWriterAssertions.Corpus();
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.6–§7.5.8: Every reader fixture retains original bytes and its last xref style")]
    [MemberData(nameof(Corpus))]
    public void Write_ReaderCorpus_AppendsTrackedEditsAndPreservesOriginal(string name, byte[] bytes, int pages)
    {
        // Arrange
        name.ShouldNotBeNullOrEmpty();
        using var source = new MemoryStream(bytes);
        using var document = new PdfReader().Read(source);
        var page = document.Pages[0];
        page.Dictionary[PdfName.Create("Rotate"u8)] = PdfInteger.Create(270);
        page.Resources[PdfName.Create("Edited"u8)] = PdfBoolean.Create(true);
        page.MediaBox[2] = PdfInteger.Create(456);
        var info = new PdfDictionary(); info[PdfName.Create("Title"u8)] = PdfString.Create("incremental"u8);
        document.Trailer[PdfName.Create("Info"u8)] = PdfIndirectReference.Create(document.Add(info));
        using var output = new MemoryStream();
        // Act
        PdfWriter.Create(new() { Incremental = true }).Write(output, document); var savedBytes = output.ToArray(); output.Position = 0;
        using var saved = new PdfReader().Read(output);
        // Assert
        savedBytes.AsSpan(0, bytes.Length).ToArray().ShouldBe(bytes);
        saved.Trailer.Get("Prev").ShouldBeOfType<PdfInteger>().Value.ShouldBe(document.StartXref);
        saved.LastCrossReferenceStyle.ShouldBe(document.LastCrossReferenceStyle);
        PdfWriterAssertions.Graphs(document, saved); saved.PageCount.ShouldBe(pages);
        saved.Pages[0].Rotate.ShouldBe(270);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.4 and §7.5.6: Several Prev revisions retain replacements and freed generations")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Write_SeveralRevisions_ResolvesNewestAndFreeEntries(int style)
    {
        // Arrange
        var bytes = style == 0 ? PdfFixtureBuilder.Classic(new[] { PdfFixtureBuilder.BasicObjects[0], PdfFixtureBuilder.BasicObjects[1], PdfFixtureBuilder.BasicObjects[2], "(old)" }) :
            PdfFixtureBuilder.XrefStream(objectStream: true, hybrid: style == 2);
        var freedNumber = style == 0 ? 4 : 6;
        var lastAdded = default(PdfObjectIdentifier);
        for (var revision = 1; revision <= 4; revision++)
        {
            using var source = new MemoryStream(bytes);
            using var document = new PdfReader().Read(source);
            if (revision == 1) { document.Free(PdfObjectIdentifier.Create(freedNumber)); }
            else { document.Replace(lastAdded, PdfInteger.Create(revision)); }
            lastAdded = document.Add(PdfInteger.Create(revision * 10));
            using var output = new MemoryStream();
            // Act
            PdfWriter.Create(new() { Incremental = true }).Write(output, document); var next = output.ToArray(); output.Position = 0;
            using var saved = new PdfReader().Read(output);
            // Assert
            next.AsSpan(0, bytes.Length).ToArray().ShouldBe(bytes);
            saved.Trailer.Get("Prev").ShouldBeOfType<PdfInteger>().Value.ShouldBe(document.StartXref);
            saved.Resolve(PdfObjectIdentifier.Create(freedNumber)).ShouldBeOfType<PdfNull>();
            saved.CrossReferences[freedNumber].Identifier.Generation.ShouldBe(1);
            saved.CrossReferences[0].NextFreeObjectNumber.ShouldBe(freedNumber);
            saved.Resolve(lastAdded).ShouldBeOfType<PdfInteger>().Value.ShouldBe(revision * 10);
            saved.LastCrossReferenceStyle.ShouldBe(document.LastCrossReferenceStyle);
            PdfWriterAssertions.Graphs(document, saved);
            bytes = next;
        }
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.8 and §7.5.6: Untouched opaque stream bytes are copied once with bounded reads")]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_UntouchedOpaqueStream_CopiesWithoutDecodingOrMaterialization(bool incremental)
    {
        // Arrange: deliberately unsupported codec, and payload larger than the configured decode limit.
        const int payloadLength = 2 * 1024 * 1024;
        var bytes = PdfFixtureBuilder.Classic(new[] { PdfFixtureBuilder.BasicObjects[0], PdfFixtureBuilder.BasicObjects[1], PdfFixtureBuilder.BasicObjects[2],
            $"<< /Length {payloadLength} /Filter /DCTDecode /DecodeParms << /Example 1 >> >>\nstream\n{new string('x', payloadLength)}\nendstream" });
        var start = Encoding.Latin1.GetString(bytes).IndexOf("stream\n", StringComparison.Ordinal) + 7;
        using var source = new PdfCopyProbeStream(bytes, start, payloadLength);
        using var document = PdfReader.Create(new() { MaxDecodedBytes = 32 }).Read(source);
        var stream = document.Resolve(PdfObjectIdentifier.Create(4)).ShouldBeOfType<PdfStream>();
        // Resolve metadata before measuring: parser read-ahead may overlap the start of a lazy payload.
        foreach (var entry in document.CrossReferences.Values)
        {
            if (entry.Kind != PdfCrossReferenceKind.Free) { document.Resolve(entry.Identifier); }
        }
        document.Catalog[PdfName.Create("Edited"u8)] = PdfBoolean.Create(true);
        source.Measure();
        using var output = new MemoryStream();
        // Act
        PdfWriter.Create(new() { Incremental = incremental }).Write(output, document);
        // Assert
        source.PayloadBytesRead.ShouldBe(payloadLength);
        source.MaxRead.ShouldBeLessThanOrEqualTo(131072);
        stream.Data.IsMaterialized.ShouldBeFalse();
        output.Position = 0; using var saved = new PdfReader().Read(output);
        var copied = saved.Resolve(PdfObjectIdentifier.Create(4)).ShouldBeOfType<PdfStream>();
        PdfWriterAssertions.Object(stream.Dictionary.Get("Filter")!, copied.Dictionary.Get("Filter")!);
        PdfWriterAssertions.Object(stream.Dictionary.Get("DecodeParms")!, copied.Dictionary.Get("DecodeParms")!);
        copied.Data.ToArray(payloadLength).ShouldBe(bytes.AsSpan(start, payloadLength).ToArray());
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.5–§7.5.6: Removed trailer entries do not reappear through Prev")]
    public void Write_RemovedInfo_EmitsNullTombstone()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.Classic(trailer: "/Info << /Title (old) >>"));
        using var document = new PdfReader().Read(source); document.Trailer.Remove(PdfName.Create("Info"u8));
        using var output = new MemoryStream();
        // Act
        PdfWriter.Create(new() { Incremental = true }).Write(output, document); output.Position = 0;
        using var saved = new PdfReader().Read(output);
        // Assert
        saved.Trailer.Get("Info").ShouldBeOfType<PdfNull>();
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.6: Repaired sources require a full save before append")]
    public void Write_RepairedSource_RejectsIncrementalBeforeOutput()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.Replace(PdfFixtureBuilder.Classic(), "startxref", "startxxxx"));
        using var document = PdfReader.Create(new() { IsLenient = true }).Read(source);
        using var output = new MemoryStream();
        // Act / Assert
        Should.Throw<ContentFormatException>(() => PdfWriter.Create(new() { Incremental = true }).Write(output, document))
            .ErrorCode.ShouldBe(ContentFormatErrorCode.Unsupported);
        output.Length.ShouldBe(0);
        new PdfWriter().Write(output, document); output.Position = 0;
        using var saved = new PdfReader().Read(output); saved.PageCount.ShouldBe(1);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.6: A supplied source origin remains the offset basis")]
    public void Write_SourceAtOffset_CopiesExactlyThePdfRange()
    {
        // Arrange
        var bytes = PdfFixtureBuilder.Classic();
        using var source = new MemoryStream(); source.Write("prefix"u8); source.Write(bytes); source.Position = 6;
        using var document = new PdfReader().Read(source);
        using var output = new MemoryStream();
        // Act
        PdfWriter.Create(new() { Incremental = true }).Write(output, document); output.Position = 0;
        using var saved = new PdfReader().Read(output);
        // Assert
        output.ToArray().AsSpan(0, bytes.Length).ToArray().ShouldBe(bytes);
        saved.PageCount.ShouldBe(1); saved.Trailer.Get("Prev").ShouldBeOfType<PdfInteger>().Value.ShouldBe(document.StartXref);
    }
}
