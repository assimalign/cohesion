using System;
using System.Buffers.Binary;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Heif.Tests;

public class HeifDerivedImageTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Heif] - Read: ISO/IEC 23008-12 grid truncation reports the absolute fragmented-extent position")]
    public void Read_FragmentedTruncatedGrid_ReportsAbsolutePosition()
    {
        // Arrange: omit two middle bytes; the missing final height is at the end of idat.
        var bytes = HeifMalformedFixtures.Mutate("grid", document =>
        {
            var meta = HeifFixtures.Find<MetaBox>(document.Boxes);
            HeifFixtures.Find<ItemLocationBox>(meta.Children).Entries[0].Extents =
                [new() { Offset = 0, Length = 4 }, new() { Offset = 6, Length = 2 }];
        });
        using var structuralSource = new MemoryStream(bytes);
        using var structural = BmffDocument.Load(structuralSource, registry: HeifBoxRegistry.Default);
        var idat = HeifFixtures.Find<ItemDataBox>(HeifFixtures.Find<MetaBox>(structural.Boxes).Children);
        using var source = new MemoryStream(bytes);

        // Act / Assert
        var error = Should.Throw<ContentFormatException>(() => HeifDocument.Load(source, new() { Strict = true }));
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
        error.Position.ShouldBe(idat.Offset + idat.Header.HeaderSize + 8);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Heif] - Load: ISO/IEC 23008-12 image overlay derivation preserves signed 16/32-bit placements")]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_OverlayDescriptor_DecodesCanvasAndPlacements(bool large)
    {
        // Arrange
        var bytes = HeifMalformedFixtures.Mutate("grid", document =>
        {
            var meta = HeifFixtures.Find<MetaBox>(document.Boxes);
            ((ItemInfoEntryBox)HeifFixtures.Find<ItemInfoBox>(meta.Children).Children[0]).ItemType = FourCC.Parse("iovl");
            var idat = HeifFixtures.Find<ItemDataBox>(meta.Children);
            using var payload = new MemoryStream();
            payload.WriteByte(0);
            payload.WriteByte((byte)(large ? 1 : 0));
            payload.Write(new byte[] { 0, 10, 0, 20, 0, 30, 255, 255 });
            void Field(int value)
            {
                Span<byte> field = stackalloc byte[4];
                if (large)
                {
                    BinaryPrimitives.WriteInt32BigEndian(field, value);
                }
                else
                {
                    BinaryPrimitives.WriteInt16BigEndian(field, (short)value);
                }

                payload.Write(field[..(large ? 4 : 2)]);
            }
            Field(large ? 100000 : 100);
            Field(200);
            for (var i = 0; i < 4; i++) { Field(-i - 1); Field(i + 1); }
            idat.Payload = BinarySegment.Create(payload.ToArray());
            HeifFixtures.Find<ItemLocationBox>(meta.Children).Entries[0].Extents[0].Length = (ulong)payload.Length;
        });
        using var source = new MemoryStream(bytes);

        // Act
        using var document = HeifDocument.Load(source, new() { Strict = true });
        var overlay = document.PrimaryImage!.Overlay!;

        // Assert
        overlay.Width.ShouldBe(large ? 100000u : 100u);
        overlay.Height.ShouldBe(200u);
        overlay.FillValues[3].ShouldBe(ushort.MaxValue);
        overlay.Layers.Count.ShouldBe(4);
        overlay.Layers[3].HorizontalOffset.ShouldBe(-4);
        overlay.Layers[3].VerticalOffset.ShouldBe(4);
        overlay.Layers[3].Item.Id.ShouldBe(5u);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Heif] - Load: ISO/IEC 23008-12 identity derivation has one source and no payload")]
    public void Load_IdentityItem_ResolvesSingleInput()
    {
        // Arrange
        var bytes = HeifMalformedFixtures.Mutate("grid", document =>
        {
            var meta = HeifFixtures.Find<MetaBox>(document.Boxes);
            ((ItemInfoEntryBox)HeifFixtures.Find<ItemInfoBox>(meta.Children).Children[0]).ItemType = FourCC.Parse("iden");
            HeifFixtures.Find<ItemLocationBox>(meta.Children).Entries[0].Extents = [];
            HeifFixtures.Find<DerivedImageReferenceBox>(HeifFixtures.Find<ItemReferenceBox>(meta.Children).Children).ToItemIds = [2];
        });
        using var source = new MemoryStream(bytes);

        // Act
        using var document = HeifDocument.Load(source, new() { Strict = true });

        // Assert
        document.PrimaryImage!.Data.ShouldBeEmpty();
        document.PrimaryImage.IdentitySource!.Id.ShouldBe(2u);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Heif] - Load: ISO/IEC 23008-12 Exif offset and 14496-12 8.11.3 fragmented extents remain lazy")]
    public void Load_FragmentedExif_HandlesOffsetAcrossExtents()
    {
        // Arrange
        var bytes = HeifMalformedFixtures.Mutate("metadata", document =>
        {
            var meta = HeifFixtures.Find<MetaBox>(document.Boxes);
            var location = HeifFixtures.Find<ItemLocationBox>(meta.Children);
            var original = location.Entries[1].Extents[0];
            location.Entries[1].Extents = [new() { Offset = original.Offset, Length = 2 }, new() { Offset = original.Offset + 2, Length = 4 }, new() { Offset = original.Offset + 6, Length = original.Length - 6 }];
        });
        using var source = new MemoryStream(bytes);

        // Act
        using var document = HeifDocument.Load(source, new() { Strict = true });
        var exif = document.ExifItems[0];

        // Assert
        exif.Data.Count.ShouldBe(3);
        exif.ExifData.Count.ShouldBe(1);
        exif.ExifData[0].IsMaterialized.ShouldBeFalse();
        exif.ExifData[0].ToArray(20).ShouldBe(new byte[] { 0x49, 0x49, 42, 0, 8, 0, 0, 0 });
    }
}
