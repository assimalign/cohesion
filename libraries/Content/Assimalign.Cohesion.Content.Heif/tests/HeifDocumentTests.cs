using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Heif.Tests;

public class HeifDocumentTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Heif] - Load/Save: ISO/IEC 23008-12 item model and AVIF 9.1 retain container bytes")]
    [InlineData("heic")]
    [InlineData("avif")]
    [InlineData("grid")]
    [InlineData("metadata")]
    public void LoadSave_GoldenContainers_PreservesBytes(string kind)
    {
        // Arrange
        var bytes = HeifFixtures.Create(kind);
        using var source = new MemoryStream(bytes);

        // Act
        using var document = HeifDocument.Load(source, new() { Strict = true });
        using var output = new MemoryStream();
        document.Save(output);

        // Assert
        output.ToArray().ShouldBe(bytes);
        document.PrimaryImage!.Id.ShouldBe(1u);
        document.Diagnostics.ShouldBeEmpty();
        document.PrimaryImage.Data[0].IsMaterialized.ShouldBeFalse();
        if (kind == "grid")
        {
            document.PrimaryImage.Grid!.Rows.ShouldBe(2);
            document.PrimaryImage.Grid.Columns.ShouldBe(2);
            document.PrimaryImage.Grid.Tiles.Count.ShouldBe(4);
            document.PrimaryImage.Grid.Tiles[3].Id.ShouldBe(5u);
            document.GetThumbnails(1)[0].Id.ShouldBe(6u);
        }
        if (kind == "avif") { document.IsAvif.ShouldBeTrue(); document.GetAlphaImages(1)[0].Id.ShouldBe(2u); }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Heif] - RemoveExif: ISO/IEC 23008-12 Exif and 14496-12 8.11.3 relocation retain image bytes")]
    public void RemoveExif_MetadataBeforeMedia_RelocatesUnchangedImage()
    {
        // Arrange
        using var source = new MemoryStream(HeifFixtures.Create("metadata"));
        using var document = HeifDocument.Load(source, new() { Strict = true });
        var image = HeifFixtures.Data(document.PrimaryImage!);
        var metadata = document.ExifItems[0];
        metadata.ExifData[0].ToArray(32).ShouldBe(new byte[] { 0x49, 0x49, 42, 0, 8, 0, 0, 0 });

        // Act
        document.RemoveExif().ShouldBe(1);
        using var output = new MemoryStream();
        document.Save(output);
        output.Position = 0;
        using var reloaded = HeifDocument.Load(output, new() { Strict = true });

        // Assert
        reloaded.ExifItems.ShouldBeEmpty();
        reloaded.XmpItems.Count.ShouldBe(1);
        HeifFixtures.Data(reloaded.PrimaryImage!).ShouldBe(image);
        HeifFixtures.Data(document.PrimaryImage!).ShouldBe(image);
        output.Length.ShouldBeLessThan(source.Length);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Heif] - Replace: ISO/IEC 23008-12 Exif/XMP and 14496-12 8.11.11 idat retain image payload")]
    public async Task ReplaceMetadata_AppendIdat_SavesRepeatedly()
    {
        // Arrange
        using var source = new MemoryStream(HeifFixtures.Create("metadata"));
        using var document = HeifDocument.Load(source, new() { Strict = true });
        var original = HeifFixtures.Data(document.PrimaryImage!);
        var xmp = Encoding.UTF8.GetBytes("<updated/>");

        // Act
        document.ReplaceExif(2, BinarySegment.Create(new byte[] { 0, 0, 0, 0, 0x4d, 0x4d, 0, 42 }));
        document.ReplaceXmp(3, BinarySegment.Create(xmp));
        using var first = new MemoryStream();
        await document.SaveAsync(first, CancellationToken.None);
        using var second = new MemoryStream();
        await document.SaveAsync(second, CancellationToken.None);
        second.Position = 0;
        using var reloaded = HeifDocument.Load(second, new() { Strict = true });

        // Assert
        second.ToArray().ShouldBe(first.ToArray());
        HeifFixtures.Data(reloaded.PrimaryImage!).ShouldBe(original);
        HeifFixtures.Data(reloaded.GetItem(3)!).ShouldBe(xmp);
        reloaded.GetItem(2)!.ExifData[0].ToArray(20).ShouldBe(new byte[] { 0x4d, 0x4d, 0, 42 });
        document.RemoveXmp().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Heif] - Edit: ISO/IEC 23008-12 thumbnails and transformative properties do not change shared images")]
    public void Edit_ThumbnailAndOrientation_RoundTrips()
    {
        // Arrange
        using var source = new MemoryStream(HeifFixtures.Create("heic"));
        using var document = HeifDocument.Load(source, new() { Strict = true });
        var original = HeifFixtures.Data(document.PrimaryImage!);

        // Act
        var thumbnail = document.AddThumbnail(1, FourCC.Parse("hvc1"), BinarySegment.Create(new byte[] { 7, 8, 9 }),
            [new ImageSpatialExtentsBox { Width = 8, Height = 8 }, new HevcConfigurationBox { Payload = BinarySegment.Create(new byte[] { 1 }) }]);
        document.SetRotation(1, 3);
        document.SetMirror(1, 1);
        using var output = new MemoryStream();
        document.Save(output);
        output.Position = 0;
        using var reloaded = HeifDocument.Load(output, new() { Strict = true });

        // Assert
        reloaded.GetThumbnails(1).Count.ShouldBe(1);
        HeifFixtures.Data(reloaded.PrimaryImage!).ShouldBe(original);
        HeifFixtures.Data(reloaded.GetItem(thumbnail.Id)!).ShouldBe(new byte[] { 7, 8, 9 });
        reloaded.PrimaryImage!.Properties.ShouldContain(x => x.Box is ImageRotationBox && ((ImageRotationBox)x.Box).Angle == 3);
        reloaded.PrimaryImage.Properties.ShouldContain(x => x.Box is ImageMirrorBox && ((ImageMirrorBox)x.Box).Axis == 1);
        reloaded.RemoveThumbnail(thumbnail.Id);
        reloaded.SetMirror(1, null);
        using var removed = new MemoryStream();
        reloaded.Save(removed);
        removed.Position = 0;
        using var final = HeifDocument.Load(removed, new() { Strict = true });
        final.GetThumbnails(1).ShouldBeEmpty();
        final.PrimaryImage!.Properties.ShouldNotContain(x => x.Box is ImageMirrorBox);
    }
}
