using System;
using System.IO;

namespace Assimalign.Cohesion.Content.Pdf.Tests;

// Goldens are produced only by an explicit developer invocation of Build; tests never rewrite them.
// Every emitted PDF contains a PdfWriter producer comment and the exact layout it used.
internal static class PdfWriterGoldenBuilder
{
    internal static readonly string[] Names =
    {
        "WRITER_TABLE", "WRITER_XREF", "WRITER_OBJECTS", "WRITER_INCREMENTAL_TABLE",
        "WRITER_INCREMENTAL_STREAM", "WRITER_INCREMENTAL_HYBRID"
    };
    internal static byte[] Build(string name)
    {
        var incremental = name.StartsWith("WRITER_INCREMENTAL_", StringComparison.Ordinal);
        var bytes = name == "WRITER_INCREMENTAL_HYBRID" ? PdfFixtureBuilder.XrefStream(objectStream: true, hybrid: true) :
            name == "WRITER_INCREMENTAL_STREAM" ? Full(PdfFixtureBuilder.InheritedPages(), true, true) : PdfFixtureBuilder.InheritedPages();
        using var source = new MemoryStream(bytes);
        using var document = new PdfReader().Read(source);
        var info = new PdfDictionary(); info[PdfName.Create("Title"u8)] = PdfString.Create("PDF writer golden"u8);
        document.Trailer[PdfName.Create("Info"u8)] = PdfIndirectReference.Create(document.Add(info));
        if (incremental) { document.Pages[0].Dictionary[PdfName.Create("Rotate"u8)] = PdfInteger.Create(270); }
        using var output = new MemoryStream();
        PdfWriter.Create(new()
        {
            Incremental = incremental, UseCrossReferenceStream = name is "WRITER_XREF" or "WRITER_OBJECTS", PackObjects = name == "WRITER_OBJECTS"
        }).Write(output, document);
        return output.ToArray();
    }
    private static byte[] Full(byte[] bytes, bool xref, bool pack)
    {
        using var source = new MemoryStream(bytes); using var document = new PdfReader().Read(source);
        using var output = new MemoryStream(); PdfWriter.Create(new() { UseCrossReferenceStream = xref, PackObjects = pack }).Write(output, document);
        return output.ToArray();
    }
}
