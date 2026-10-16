using System;
using System.IO;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Ebml.Tests.TestObjects;

// License-clean fixture: generic IDs and bytes built in code, using this package's writer.
// No media profile or downloaded content is involved.
internal static class EbmlTestData
{
    internal static VInt Id(ulong encoded) => VInt.FromEncoded(encoded);
    internal static EbmlSchema Schema()
    {
        var builder = new EbmlSchemaBuilder().Include(EbmlSchema.Header);
        builder.Add(EbmlElementDefinition.Create(Id(0xA0), "Root", EbmlElementType.MasterElement, unknownSizeAllowed: true));
        builder.Add(EbmlElementDefinition.Create(Id(0xA8), "Group", EbmlElementType.MasterElement, [Id(0xA0)], unknownSizeAllowed: true));
        var types = new[] { EbmlElementType.UnsignedInteger, EbmlElementType.SignedInteger, EbmlElementType.Float,
            EbmlElementType.AsciiString, EbmlElementType.Utf8String, EbmlElementType.Date, EbmlElementType.Binary };
        for (var i = 0; i < types.Length; i++)
        {
            builder.Add(EbmlElementDefinition.Create(Id((ulong)(0xA1 + i)), "Value" + i, types[i], [Id(0xA0)]));
        }
        builder.Add(EbmlElementDefinition.Create(Id(0xA9), "Sibling", EbmlElementType.UnsignedInteger, [Id(0xA0)]));
        builder.Add(EbmlElementDefinition.Create(Id(0xAA), "Child", EbmlElementType.UnsignedInteger, [Id(0xA8)]));
        return builder.Build();
    }

    internal static EbmlDocument Document()
    {
        var document = EbmlDocument.Create(Schema());
        document.Header.DocType = "cohesion-test";
        var root = EbmlElement.Create(Id(0xA0), EbmlElementType.MasterElement);
        root.Children.Add(EbmlElement.Create(Id(0xA1), EbmlElementType.UnsignedInteger, ulong.MaxValue));
        root.Children.Add(EbmlElement.Create(Id(0xA2), EbmlElementType.SignedInteger, -129L));
        root.Children.Add(EbmlElement.Create(Id(0xA3), EbmlElementType.Float, 1.25d));
        root.Children.Add(EbmlElement.Create(Id(0xA4), EbmlElementType.AsciiString, "text"));
        root.Children.Add(EbmlElement.Create(Id(0xA5), EbmlElementType.Utf8String, "café"));
        root.Children.Add(EbmlElement.Create(Id(0xA6), EbmlElementType.Date, -1L));
        root.Children.Add(EbmlElement.Create(Id(0xA7), EbmlElementType.Binary, BinarySegment.Create(new byte[] { 1, 2, 3 })));
        // An unregistered ID, including an intentionally wider size encoding, must survive unchanged.
        root.Children.Add(EbmlElement.Create(Id(0xB1), EbmlElementType.Binary, BinarySegment.Create(new byte[] { 9, 8, 7 })));
        root.Children[^1].SizeLength = 3;
        document.Body.Elements.Add(root);
        return document;
    }

    internal static byte[] Encode(Action<EbmlWriter> write)
    {
        using var output = new MemoryStream();
        using var writer = EbmlWriter.Create(output, Schema());
        write(writer);
        return output.ToArray();
    }
}
