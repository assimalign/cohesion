using System;
using System.Buffers.Binary;
using System.IO;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Heif.Tests;

internal static class HeifMalformedFixtures
{
    // Every malformed case starts with the local writer fixture, changes the named field,
    // then emits through BMFF. No test data is downloaded or read from developer paths.
    internal static byte[] Mutate(string kind, Action<BmffDocument> change)
    {
        using var source = new MemoryStream(HeifFixtures.Create(kind));
        using var document = BmffDocument.Load(source, registry: HeifBoxRegistry.Default);
        change(document);
        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    internal static byte[] Create(string variant)
    {
        if (variant == "overflow-offset")
        {
            var bytes = Mutate("heic", document =>
            {
                var loc = HeifFixtures.Find<ItemLocationBox>(HeifFixtures.Find<MetaBox>(document.Boxes).Children);
                loc.OffsetSize = 8;
                loc.BaseOffsetSize = 8;
            });
            using var source = new MemoryStream(bytes);
            using var parsed = BmffDocument.Load(source, registry: HeifBoxRegistry.Default);
            var location = HeifFixtures.Find<ItemLocationBox>(HeifFixtures.Find<MetaBox>(parsed.Boxes).Children);
            // v1 base offset follows prefix, widths, count, ID, method and data reference.
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan((int)location.Offset + 22), ulong.MaxValue);
            return bytes;
        }
        return Mutate(variant is "grid-count" or "cycle" ? "grid" : variant == "exif-offset" ? "metadata" : "heic", document =>
    {
        var meta = HeifFixtures.Find<MetaBox>(document.Boxes);
        var info = HeifFixtures.Find<ItemInfoBox>(meta.Children);
        var location = HeifFixtures.Find<ItemLocationBox>(meta.Children);
        var props = HeifFixtures.Find<ItemPropertiesBox>(meta.Children);
        var ipma = HeifFixtures.Find<ItemPropertyAssociationBox>(props.Children);
        var ipco = HeifFixtures.Find<ItemPropertyContainerBox>(props.Children);
        var refs = HeifFixtures.Find<ItemReferenceBox>(meta.Children);
        switch (variant)
        {
            case "missing-property": ipma.Entries[0].Associations[0].PropertyIndex = 99; break;
            case "outside-file": location.Entries[0].Extents[0].Offset = 1_000_000; break;
            case "grid-count": HeifFixtures.Find<DerivedImageReferenceBox>(refs.Children).ToItemIds = [2, 3, 4]; break;
            case "cycle": HeifFixtures.Find<DerivedImageReferenceBox>(refs.Children).ToItemIds = [1, 3, 4, 5]; break;
            case "hidden-primary": ((ItemInfoEntryBox)info.Children[0]).Flags = 1; break;
            case "missing-primary": HeifFixtures.Find<PrimaryItemReferenceBox>(meta.Children).ItemId = 300; break;
            case "essential-unknown": ipco.Children[0] = UnknownBox.Create(BmffBoxHeader.Create((BmffBoxType)0x7a7a7a7a, 1), BinarySegment.Create(new byte[] { 42 })); break;
            case "item-offset": location.Entries[0].ConstructionMethod = 2; break;
            case "external-data": location.Entries[0].DataReferenceIndex = 1; break;
            case "protected": ((ItemInfoEntryBox)info.Children[0]).ItemProtectionIndex = 1; break;
            case "duplicate-id": info.Children.Add(new ItemInfoEntryBox { ItemId = 1, ItemType = FourCC.Parse("hvc1"), ItemName = "duplicate" }); break;
            case "exif-offset":
                var mdat = HeifFixtures.Find<MediaDataBox>(document.Boxes);
                var bytes = mdat.Payload.ToArray(4096);
                // Image is five bytes; Exif's four-byte offset starts immediately after it.
                bytes.AsSpan(5, 4).Fill(255);
                mdat.Payload = BinarySegment.Create(bytes);
                break;
        }
    });
    }
}
