using System;
using System.Collections.Generic;
using System.IO;

using Shouldly;

namespace Assimalign.Cohesion.Content.Pdf.Tests;

internal static class PdfWriterAssertions
{
    internal static IEnumerable<object[]> Corpus()
    {
        foreach (var item in PdfCorpusTests.Corpus()) { yield return item; }
        foreach (var name in new[] { "CLASSIC", "XREF_PREDICTOR12", "OBJECT_STREAM", "INHERITED_PAGES" })
        {
            yield return new object[] { name, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestObjects", "Golden", name + ".pdf")), name == "INHERITED_PAGES" ? 2 : 1 };
        }
    }
    internal static void Graphs(PdfDocument expected, PdfDocument actual)
    {
        foreach (var entry in expected.CrossReferences.Values)
        {
            if (entry.Kind == PdfCrossReferenceKind.Free) { continue; }
            Object(expected.Resolve(entry.Identifier), actual.Resolve(entry.Identifier));
        }
        actual.PageCount.ShouldBe(expected.PageCount);
        var originalPages = expected.Pages; var savedPages = actual.Pages;
        for (var i = 0; i < originalPages.Count; i++)
        {
            savedPages[i].Identifier.ShouldBe(originalPages[i].Identifier);
            savedPages[i].Rotate.ShouldBe(originalPages[i].Rotate);
            Object(originalPages[i].Resources, savedPages[i].Resources);
            Object(originalPages[i].MediaBox, savedPages[i].MediaBox);
            Object(originalPages[i].CropBox, savedPages[i].CropBox);
        }
    }
    internal static void Object(IPdfObject expected, IPdfObject actual)
    {
        switch (expected)
        {
            case PdfNull: actual.ShouldBeOfType<PdfNull>(); break;
            case PdfBoolean value: actual.ShouldBeOfType<PdfBoolean>().Value.ShouldBe(value.Value); break;
            case PdfInteger value: actual.ShouldBeOfType<PdfInteger>().Value.ShouldBe(value.Value); break;
            case PdfReal value: actual.ShouldBeOfType<PdfReal>().Value.ShouldBe(value.Value); break;
            case PdfName value: actual.ShouldBeOfType<PdfName>().Bytes.ToArray().ShouldBe(value.Bytes.ToArray()); break;
            case PdfString value: actual.ShouldBeOfType<PdfString>().Bytes.ToArray().ShouldBe(value.Bytes.ToArray()); break;
            case PdfIndirectReference value: actual.ShouldBeOfType<PdfIndirectReference>().Identifier.ShouldBe(value.Identifier); break;
            case PdfArray value:
                var array = actual.ShouldBeOfType<PdfArray>(); array.Count.ShouldBe(value.Count);
                for (var i = 0; i < value.Count; i++) { Object(value[i], array[i]); } break;
            case PdfDictionary value: Dictionary(value, actual.ShouldBeOfType<PdfDictionary>()); break;
            case PdfStream value:
                var stream = actual.ShouldBeOfType<PdfStream>();
                Dictionary(value.Dictionary, stream.Dictionary);
                stream.Data.ToArray(16 * 1024 * 1024).ShouldBe(value.Data.ToArray(16 * 1024 * 1024)); break;
            default: throw new InvalidOperationException();
        }
    }
    private static void Dictionary(PdfDictionary expected, PdfDictionary actual)
    {
        actual.Count.ShouldBe(expected.Count);
        foreach (var pair in expected)
        {
            actual.ContainsKey(pair.Key).ShouldBeTrue(); Object(pair.Value, actual[pair.Key]);
        }
    }
}
