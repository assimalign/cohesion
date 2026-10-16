using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Heif.Tests;

// Original writer-generated ISO 23008-12 items; the distinctive Exif text is synthetic metadata.
internal static class HeifErasureFixtures
{
    internal const string Marker = "COHESION_EXIF_PRIVATE_LOCATION_8C24F7A1";
    internal const string XmpMarker = "COHESION_XMP_PRIVATE_71D9";
    internal static byte[] WithAuxiliaryOffset(bool removedTarget, bool fragmented)
    {
        using var source = new MemoryStream(Create(false));
        using var document = BmffDocument.Load(source, registry: HeifBoxRegistry.Default);
        var locations = HeifFixtures.Find<ItemLocationBox>(HeifFixtures.Find<MetaBox>(document.Boxes).Children);
        var entry = locations.Entries[removedTarget ? 1 : 3];
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8), (uint)(entry.BaseOffset + entry.Extents[0].Offset));
        // The auxiliary offset lives in an extra sample table; no timed-track interpretation is needed.
        var table = new SampleTableBox(0, 0);
        table.Children.Add(UnknownBox.Create(BmffBoxHeader.Create((BmffBoxType)0x7361696f, payload.Length), BinarySegment.Create(payload)));
        var movie = new MovieBox(0, 0);
        movie.Children.Add(table);
        document.Boxes.Add(movie);
        if (fragmented)
        {
            var exif = locations.Entries[1].Extents[0];
            locations.Entries[1].Extents = [new() { Offset = exif.Offset, Length = 4 }, new() { Offset = exif.Offset + 4, Length = exif.Length - 4 }];
            // Growing iloc moves mdat; let the document relocate the independently authored addresses.
        }
        using var output = new MemoryStream();
        // Parse the auxiliary box before the relocation planner uses it.
        using (var raw = new MemoryStream(Write(table)))
        {
            using var parsed = BmffDocument.Load(raw);
            movie.Children[0] = parsed.Boxes[0];
        }
        // The new table is absent from the old layout, but its absolute field uses the old media layout.
        document.Save(output);
        return output.ToArray();
    }
    internal static byte[] Create(bool inIdat, int sharing = 0, bool secondMdat = false, bool zeroLength = false)
    {
        using var source = new MemoryStream(HeifFixtures.Create("metadata"));
        using var document = BmffDocument.Load(source, registry: HeifBoxRegistry.Register(BmffBoxRegistry.CreateBuilder()).Build());
        var meta = HeifFixtures.Find<MetaBox>(document.Boxes);
        var info = HeifFixtures.Find<ItemInfoBox>(meta.Children);
        info.Children.Add(new ItemInfoEntryBox { ItemId = 4, ItemType = FourCC.Parse("hvc1"), ItemName = "Retained image" });
        var properties = HeifFixtures.Find<ItemPropertiesBox>(meta.Children);
        var associations = HeifFixtures.Find<ItemPropertyAssociationBox>(properties.Children);
        var values = new ItemPropertyAssociationBox.Entry[associations.Entries.Length + 1];
        associations.Entries.CopyTo(values, 0);
        values[^1] = new() { ItemId = 4, Associations = associations.Entries[0].Associations };
        associations.Entries = values;
        var primary = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 };
        var exif = new byte[4 + Marker.Length];
        Encoding.ASCII.GetBytes(Marker).CopyTo(exif, 4);
        var xmp = Encoding.ASCII.GetBytes(XmpMarker);
        var image = new byte[] { 0x91, 0x92, 0x93, 0x94 };
        using var data = new MemoryStream();
        data.Write(primary);
        data.Write(exif);
        data.Write(xmp);
        var imageOffset = data.Length;
        if (!secondMdat) { data.Write(image); }
        var media = HeifFixtures.Find<MediaDataBox>(document.Boxes);
        MediaDataBox? second = null;
        if (inIdat)
        {
            media.Payload = default;
            meta.Children.Add(new ItemDataBox { Payload = BinarySegment.Create(data.ToArray()) });
        }
        else { media.Payload = BinarySegment.Create(data.ToArray()); }
        if (secondMdat)
        {
            second = new MediaDataBox(0, 0) { Payload = BinarySegment.Create(image) };
            document.Boxes.Add(second);
        }
        var locations = HeifFixtures.Find<ItemLocationBox>(meta.Children);
        locations.BaseOffsetSize = 4;
        locations.Entries =
        [
            new() { ItemId = 1, Extents = [new() { Length = 5 }] },
            new() { ItemId = 2, Extents = [new() { Offset = 5, Length = (ulong)exif.Length }] },
            new() { ItemId = 3, Extents = [new() { Offset = (ulong)(5 + exif.Length), Length = (ulong)xmp.Length }] },
            new() { ItemId = 4, Extents = [new() { Offset = (ulong)imageOffset, Length = zeroLength ? 0UL : (ulong)image.Length }] }
        ];
        if (sharing != 0)
        {
            locations.Entries[3].Extents = [new() { Offset = sharing == 1 ? 5UL : 13UL, Length = sharing == 1 ? (ulong)exif.Length : 8UL }];
        }
        var mediaStart = (ulong)(Write(document.Boxes[0]).Length + Write(meta).Length + 8);
        foreach (ref var entry in locations.Entries.AsSpan())
        {
            entry.ConstructionMethod = inIdat ? (ushort)1 : (ushort)0;
            entry.BaseOffset = inIdat ? 0 : mediaStart;
        }
        if (second is not null)
        {
            locations.Entries[3].BaseOffset = mediaStart + (ulong)media.Payload.Length + 8;
            locations.Entries[3].Extents[0].Offset = 0;
        }
        // Put a retained extent's base inside the deleted range: only its effective address matters.
        if (!inIdat && sharing == 0 && second is null)
        {
            locations.Entries[3].BaseOffset += 5;
            locations.Entries[3].Extents[0].Offset -= 5;
        }
        using var output = new MemoryStream();
        using var writer = BmffWriter.Create(output);
        foreach (var box in document.Boxes) { writer.Write(box); }
        return output.ToArray();
    }

    private static byte[] Write(BmffBox box)
    {
        using var output = new MemoryStream();
        using var writer = BmffWriter.Create(output);
        writer.Write(box);
        return output.ToArray();
    }
}
