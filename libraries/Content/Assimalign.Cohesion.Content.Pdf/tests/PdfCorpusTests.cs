using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Pdf.Tests;

public sealed class PdfCorpusTests
{
    public static IEnumerable<object[]> Corpus()
    {
        yield return new object[] { "classic", PdfFixtureBuilder.Classic(), 1 };
        yield return new object[] { "xref-predictor12", PdfFixtureBuilder.XrefStream(), 1 };
        yield return new object[] { "xref-no-predictor", PdfFixtureBuilder.XrefStream(predictor: false), 1 };
        yield return new object[] { "object-stream", PdfFixtureBuilder.XrefStream(objectStream: true), 1 };
        yield return new object[] { "hybrid", PdfFixtureBuilder.XrefStream(objectStream: true, hybrid: true), 1 };
        yield return new object[] { "incremental-table", PdfFixtureBuilder.Incremental(), 1 };
        yield return new object[] { "incremental-stream", PdfFixtureBuilder.Incremental(stream: true), 1 };
        yield return new object[] { "inherited-pages", PdfFixtureBuilder.InheritedPages(), 2 };
        yield return new object[] { "indirect-length", PdfFixtureBuilder.IndirectLength(), 1 };
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.4–§7.5.8 and §7.7.3: Generated corpus resolves every current object")]
    [MemberData(nameof(Corpus))]
    public void Read_GeneratedCorpus_ResolvesEveryObjectAndCountsPages(string name, byte[] bytes, int pages)
    {
        // Arrange
        name.ShouldNotBeNullOrEmpty();
        using var source = new MemoryStream(bytes);
        // Act
        using var document = new PdfReader().Read(source);
        // Assert
        foreach (var entry in document.CrossReferences.Values)
        {
            if (entry.Kind == PdfCrossReferenceKind.Free) { continue; }
            var first = document.Resolve(entry.Identifier);
            first.ShouldNotBeOfType<PdfNull>();
            document.Resolve(entry.Identifier).ShouldBeSameAs(first);
        }
        document.PageCount.ShouldBe(pages);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.7: Compressed objects are extracted lazily with identity caching")]
    public void Resolve_ObjectStream_CachesMemberAndPreservesContainer()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.XrefStream(objectStream: true));
        using var document = new PdfReader().Read(source);
        // Act
        var value = document.Resolve(PdfObjectIdentifier.Create(6)).ShouldBeOfType<PdfDictionary>();
        // Assert
        value[PdfName.Create("Example"u8)].ShouldBeOfType<PdfInteger>().Value.ShouldBe(42);
        document.CrossReferences[6].ObjectStreamIndex.ShouldBe(1);
        document.Resolve(PdfObjectIdentifier.Create(6)).ShouldBeSameAs(value);
        document.Resolve(PdfObjectIdentifier.Create(4)).ShouldBeOfType<PdfStream>().Data.IsMaterialized.ShouldBeFalse();
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.6: New revisions and free entries shadow older objects")]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_IncrementalRevision_PrefersNewestAndHonorsFreeEntry(bool stream)
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.Incremental(stream));
        using var document = new PdfReader().Read(source);
        // Act / Assert
        document.Pages[0].Rotate.ShouldBe(90);
        document.Resolve(PdfObjectIdentifier.Create(4)).ShouldBeOfType<PdfNull>();
        document.CrossReferences[4].Identifier.Generation.ShouldBe(1);
        document.Resolve(PdfObjectIdentifier.Create(5)).ShouldBeOfType<PdfString>().DecodeText(Encoding.ASCII).ShouldBe("new revision");
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.7.3: Three-level inheritance uses the nearest ancestor")]
    public void Read_PageTree_ResolvesInheritedAttributesWithoutFlattening()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.InheritedPages());
        using var document = new PdfReader().Read(source);
        // Act
        var pages = document.Pages;
        // Assert
        pages.Count.ShouldBe(2);
        pages[0].Rotate.ShouldBe(180);
        pages[0].Resources.ContainsKey(PdfName.Create("Nearest"u8)).ShouldBeTrue();
        pages[0].MediaBox[2].ShouldBeOfType<PdfInteger>().Value.ShouldBe(200);
        pages[0].CropBox[0].ShouldBeOfType<PdfInteger>().Value.ShouldBe(5);
        pages[0].Dictionary.ContainsKey(PdfName.Create("Resources"u8)).ShouldBeFalse();
        pages[1].Resources.ShouldBeSameAs(document.Resolve(PdfObjectIdentifier.Create(9)));
        pages[1].Rotate.ShouldBe(-90);
        pages[1].CropBox[2].ShouldBeOfType<PdfInteger>().Value.ShouldBe(100);
        // Editing the retained tree is reflected in the next page view.
        document.Resolve(PdfObjectIdentifier.Create(4)).ShouldBeOfType<PdfDictionary>()[PdfName.Create("Rotate"u8)] = PdfInteger.Create(270);
        document.Pages[0].Rotate.ShouldBe(270);
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.3.8: Indirect Length resolves before the lazy segment is created")]
    public void Resolve_IndirectLength_DecodesExactPayload()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.IndirectLength());
        using var document = new PdfReader().Read(source);
        // Act / Assert
        document.Resolve(PdfObjectIdentifier.Create(4)).ShouldBeOfType<PdfStream>().Decode().ToArray().ShouldBe("Hello"u8.ToArray());
    }
    [Fact(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5.5 and §7.6: Encrypted structure loads but strings and streams are Unsupported")]
    public void Read_EncryptTrailer_LoadsStructureAndGuardsPayloadAccess()
    {
        // Arrange
        using var source = new MemoryStream(PdfFixtureBuilder.Encrypted());
        // Act
        using var document = new PdfReader().Read(source);
        // Assert
        document.IsEncrypted.ShouldBeTrue();
        document.PageCount.ShouldBe(1);
        var secret = document.Catalog[PdfName.Create("Secret"u8)].ShouldBeOfType<PdfString>();
        Should.Throw<ContentFormatException>(() => secret.Bytes).ErrorCode.ShouldBe(ContentFormatErrorCode.Unsupported);
        var stream = document.Resolve(PdfObjectIdentifier.Create(5)).ShouldBeOfType<PdfStream>();
        Should.Throw<ContentFormatException>(() => stream.Data).ErrorCode.ShouldBe(ContentFormatErrorCode.Unsupported);
        Should.Throw<ContentFormatException>(() => stream.Decode()).ErrorCode.ShouldBe(ContentFormatErrorCode.Unsupported);
        var ids = document.Trailer[PdfName.Create("ID"u8)].ShouldBeOfType<PdfArray>();
        Should.Throw<ContentFormatException>(() => ids[0].ShouldBeOfType<PdfString>().Bytes).ErrorCode.ShouldBe(ContentFormatErrorCode.Unsupported);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Pdf] - ISO 32000-2 §7.5: Golden bytes load without fixture regeneration")]
    [InlineData("CLASSIC.pdf", 1)]
    [InlineData("XREF_PREDICTOR12.pdf", 1)]
    [InlineData("OBJECT_STREAM.pdf", 1)]
    [InlineData("INHERITED_PAGES.pdf", 2)]
    public void Read_GoldenFixture_ResolvesEveryObject(string name, int count)
    {
        // Arrange: each checked-in file names its generating PdfFixtureBuilder helper in a PDF comment.
        using var source = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestObjects", "Golden", name));
        // Act
        using var document = new PdfReader().Read(source);
        // Assert
        foreach (var entry in document.CrossReferences.Values)
        {
            if (entry.Kind != PdfCrossReferenceKind.Free) { document.Resolve(entry.Identifier).ShouldNotBeOfType<PdfNull>(); }
        }
        document.PageCount.ShouldBe(count);
    }
}
