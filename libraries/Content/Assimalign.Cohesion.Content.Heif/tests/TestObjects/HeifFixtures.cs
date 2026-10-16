using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Heif.Tests;

// Original writer-generated containers. Codec payloads are numbered placeholder bytes,
// not decodable media. Each variant is generated entirely by this repository's BMFF writer.
internal static class HeifFixtures
{
    internal static byte[] Create(string kind)
    {
        using var document = new BmffDocument();
        var avif = kind == "avif";
        var ftyp = new FileTypeBox(0) { MajorBrand = FourCC.Parse(avif ? "avif" : "heic").Value, CompatibleBrands = [FourCC.Parse("mif1").Value] };
        var meta = new MetaBox(0, 0);
        var info = new ItemInfoBox(0, 0);
        var iloc = new ItemLocationBox(0, 0) { Version = 1, OffsetSize = 4, LengthSize = 4, BaseOffsetSize = 0 };
        var iprp = new ItemPropertiesBox();
        var ipco = new ItemPropertyContainerBox();
        var ipma = new ItemPropertyAssociationBox();
        var iref = new ItemReferenceBox();
        var idat = new ItemDataBox();
        using var data = new MemoryStream();
        var locations = new List<ItemLocationBox.Entry>();
        var associations = new List<ItemPropertyAssociationBox.Entry>();
        meta.Children.Add(new HandlerBox(0, 0) { HandlerType = FourCC.Parse("pict"), Name = "HEIF fixture" });
        meta.Children.Add(new PrimaryItemReferenceBox(0, 0) { ItemId = 1 });
        meta.Children.Add(info);
        meta.Children.Add(iloc);
        meta.Children.Add(iprp);
        meta.Children.Add(iref);
        iprp.Children.Add(ipco);
        iprp.Children.Add(ipma);
        ipco.Children.Add(new ImageSpatialExtentsBox { Width = 64, Height = 64 });
        ipco.Children.Add(avif ? new Av1ConfigurationBox { Payload = BinarySegment.Create(new byte[] { 0x81, 0, 0, 0 }) } : new HevcConfigurationBox { Payload = BinarySegment.Create(new byte[] { 1, 2, 3, 4 }) });
        ipco.Children.Add(new ColourInformationBox { ColourPrimaries = 1, TransferCharacteristics = 13, MatrixCoefficients = 1, FullRange = true });
        ipco.Children.Add(new PixelInformationBox { BitsPerChannel = [8, 8, 8] });

        void Item(uint id, string type, byte[] payload, ushort[] properties, bool inIdat = false, string contentType = "")
        {
            info.Children.Add(new ItemInfoEntryBox { ItemId = id, ItemType = FourCC.Parse(type), ItemName = "Item " + id, ContentType = contentType });
            var offset = data.Position;
            if (inIdat) { idat.Payload = BinarySegment.Create(payload); offset = 0; }
            else
            {
                data.Write(payload);
            }

            locations.Add(new ItemLocationBox.Entry { ItemId = id, ConstructionMethod = (ushort)(inIdat ? 1 : 0), Extents = [new() { Offset = (ulong)offset, Length = (ulong)payload.Length }] });
            var values = new ItemPropertyAssociationBox.Association[properties.Length];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = new() { PropertyIndex = properties[i], Essential = properties[i] != 3 };
            }

            associations.Add(new() { ItemId = id, Associations = values });
        }

        if (kind == "grid")
        {
            // ISO/IEC 23008-12 grid: v0/flags0, 2x2, 128x128; descriptor in idat.
            Item(1, "grid", [0, 0, 1, 1, 0, 128, 0, 128], [1], true);
            for (uint id = 2; id <= 6; id++)
            {
                Item(id, "hvc1", [(byte)id, 0x40, 0x50, (byte)(id + 10)], [1, 2, 3]);
            }

            iref.Children.Add(new DerivedImageReferenceBox { FromItemId = 1, ToItemIds = [2, 3, 4, 5] });
            iref.Children.Add(new ThumbnailReferenceBox { FromItemId = 6, ToItemIds = [1] });
            meta.Children.Add(idat);
        }
        else
        {
            Item(1, avif ? "av01" : "hvc1", [0x10, 0x20, 0x30, 0x40, 0x50], avif ? [1, 2, 3, 4] : [1, 2, 3]);
            if (avif)
            {
                ipco.Children.Add(new AuxiliaryTypeBox { AuxiliaryType = "urn:mpeg:mpegB:cicp:systems:auxiliary:alpha" });
                ipco.Children.Add(new PixelInformationBox { BitsPerChannel = [8] });
                Item(2, "av01", [0x51, 0x52, 0x53], [1, 2, 5, 6]);
                iref.Children.Add(new AuxiliaryReferenceBox { FromItemId = 2, ToItemIds = [1] });
            }
            if (kind == "metadata")
            {
                // TIFF begins 4+2 bytes into this opaque Exif item.
                Item(2, "Exif", [0, 0, 0, 2, 99, 98, 0x49, 0x49, 42, 0, 8, 0, 0, 0], []);
                Item(3, "mime", Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"), [], contentType: "application/rdf+xml");
                iref.Children.Add(new ContentDescriptionReferenceBox { FromItemId = 2, ToItemIds = [1] });
                iref.Children.Add(new ContentDescriptionReferenceBox { FromItemId = 3, ToItemIds = [1] });
            }
        }
        iloc.Entries = locations.ToArray();
        ipma.Entries = associations.ToArray();
        var mdat = new MediaDataBox(0, 0) { Payload = BinarySegment.Create(data.ToArray()) };
        document.Boxes.Add(ftyp);
        document.Boxes.Add(meta);
        document.Boxes.Add(mdat);
        // Fixed-width locations let the writer determine mdat's absolute payload start once.
        using var measured = new MemoryStream();
        document.Save(measured);
        var start = (ulong)(measured.Length - mdat.Payload.Length);
        foreach (ref var location in iloc.Entries.AsSpan())
        {
            if (location.ConstructionMethod == 0)
            {
                location.Extents[0].Offset += start;
            }
        }

        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    internal static byte[] Data(HeifItem item)
    {
        using var output = new MemoryStream();
        foreach (var segment in item.Data)
        {
            segment.CopyTo(output);
        }

        return output.ToArray();
    }

    internal static T Find<T>(IList<BmffBox> boxes) where T : BmffBox
    {
        foreach (var box in boxes) { if (box is T result)
            {
                return result;
            }
        }
        throw new InvalidOperationException("Missing fixture box.");
    }
}
