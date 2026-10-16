using System;
using System.IO;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Mpeg.Tests;

// Provenance: all fixture bytes are authored here and emitted by BmffWriter/BmffDocument.
// Codec bytes are placeholders, never downloaded or presented as decodable media.
internal static class Mp4Fixtures
{
    internal static T Container<T>(T parent, params BmffBox[] children) where T : BmffBoxComposite
    {
        foreach (var child in children) { parent.Children.Add(child); }
        return parent;
    }

    internal static byte[] Write(params BmffBox[] boxes)
    {
        using var output = new MemoryStream();
        using var writer = BmffWriter.Create(output);
        foreach (var box in boxes) { writer.Write(box); }
        return output.ToArray();
    }

    internal static UnknownBox Opaque(string code, byte[] bytes) =>
        UnknownBox.Create(BmffBoxHeader.Create((BmffBoxType)FourCC.Parse(code).Value, bytes.Length), BinarySegment.Create(bytes));

    internal static FileTypeBox Brand(string code) => new FileTypeBox(0)
    {
        MajorBrand = FourCC.Parse(code).Value,
        CompatibleBrands = [FourCC.Parse("isom").Value, FourCC.Parse(code).Value]
    };
}

