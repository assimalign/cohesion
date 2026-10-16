using System;
using System.Collections.Generic;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Heif.Tests;

internal static class HeifBoxFixtures
{
    // Writer-authored field vectors, owned by this repository; no external media.
    internal static IEnumerable<object[]> Boxes()
    {
        yield return [new ImageSpatialExtentsBox { Width = 1920, Height = 1080, TrailingData = [91] }, "ISO/IEC 23008-12 image spatial extents property"];
        yield return [new PixelInformationBox { BitsPerChannel = [10, 10, 10], TrailingData = [92] }, "ISO/IEC 23008-12 pixel information property"];
        yield return [new ColourInformationBox { ColourPrimaries = 9, TransferCharacteristics = 16, MatrixCoefficients = 9, FullRange = true, Reserved = 19, TrailingData = BinarySegment.Create(new byte[] { 93 }) }, "ISO/IEC 23008-12 colour information property"];
        yield return [new ColourInformationBox { ColourType = FourCC.FromUInt32(0x70726f66), Profile = BinarySegment.Create(new byte[] { 1, 2, 3 }) }, "ISO/IEC 14496-12 colour information ICC"];
        yield return [new ImageRotationBox { Angle = 3, Reserved = 4, TrailingData = [94] }, "ISO/IEC 23008-12 image rotation property"];
        yield return [new ImageMirrorBox { Axis = 1, Reserved = 7, TrailingData = [95] }, "ISO/IEC 23008-12 image mirror property"];
        yield return [new CleanApertureBox { WidthNumerator = 1920, HeightNumerator = 1080, HorizontalOffsetNumerator = -9, VerticalOffsetNumerator = 7, TrailingData = [96] }, "ISO/IEC 23008-12 clean aperture property"];
        yield return [new PixelAspectRatioBox { HorizontalSpacing = 4, VerticalSpacing = 3, TrailingData = [97] }, "ISO/IEC 14496-12 pixel aspect ratio box"];
        yield return [new AuxiliaryTypeBox { AuxiliaryType = "urn:mpeg:mpegB:cicp:systems:auxiliary:alpha", Subtype = [4, 5] }, "AVIF 4.1 auxiliary images"];
        yield return [new HevcConfigurationBox { Payload = BinarySegment.Create(new byte[] { 1, 2, 3, 4 }) }, "ISO/IEC 23008-12 HEVC configuration property"];
        yield return [new Av1ConfigurationBox { Payload = BinarySegment.Create(new byte[] { 0x81, 0, 0, 0 }) }, "AVIF 2.2.1 AV1 configuration property"];
        yield return [new ItemDataBox { Payload = BinarySegment.Create(new byte[] { 3, 2, 1 }) }, "ISO/IEC 14496-12 8.11.11 item data"];
        foreach (var large in new[] { false, true })
        {
            var id = large ? 70000u : 7u;
            var refs = new ItemReferenceBox { Version = (byte)(large ? 1 : 0) };
            refs.Children.Add(new ThumbnailReferenceBox { LargeIds = large, FromItemId = id, ToItemIds = [1, 2], TrailingData = [98] });
            refs.Children.Add(new AuxiliaryReferenceBox { LargeIds = large, FromItemId = id, ToItemIds = [3] });
            refs.Children.Add(new DerivedImageReferenceBox { LargeIds = large, FromItemId = id, ToItemIds = [4, 5] });
            refs.Children.Add(new ContentDescriptionReferenceBox { LargeIds = large, FromItemId = id, ToItemIds = [6] });
            refs.Children.Add(new BaseReferenceBox { LargeIds = large, FromItemId = id, ToItemIds = [7] });
            yield return [refs, "ISO/IEC 14496-12 8.11.12 item references"];
            foreach (var wide in new[] { false, true })
            {
                yield return [new ItemPropertyAssociationBox
                {
                    Version = (byte)(large ? 1 : 0), Flags = wide ? 1u : 0u, TrailingData = [99],
                    Entries = [new() { ItemId = id, Associations = [new() { PropertyIndex = (ushort)(wide ? 300 : 7), Essential = true }, new() { PropertyIndex = 0 }] }]
                }, "ISO/IEC 14496-12 8.11.14.3 item property associations"];
            }
        }
        var properties = new ItemPropertiesBox();
        var container = new ItemPropertyContainerBox();
        container.Children.Add(new ImageRotationBox { Angle = 1 });
        container.Children.Add(UnknownBox.Create(BmffBoxHeader.Create((BmffBoxType)0x7a7a7a7a, 2), BinarySegment.Create(new byte[] { 9, 8 })));
        properties.Children.Add(container);
        properties.Children.Add(new ItemPropertyAssociationBox());
        yield return [properties, "ISO/IEC 14496-12 8.11.14 item properties and property container"];
    }

}
