using System;
using System.Collections.Generic;
using System.IO;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Heif.Tests;

public class HeifCorpusTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Heif] - Corpus: ISO/IEC 23008-12 still images and AVIF 9.1 checked fixtures match their writer")]
    [InlineData("heic")]
    [InlineData("avif")]
    [InlineData("grid")]
    [InlineData("metadata")]
    public void Load_CheckedFixture_MatchesWriterAndRoundTrips(string kind)
    {
        // Arrange: provenance and the complete reproducible writer recipe are beside the fixtures.
        var expected = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestObjects", "Corpus", kind + ".bin"));
        using var source = new MemoryStream(expected);

        // Act
        using var document = HeifDocument.Load(source, new() { Strict = true });
        using var output = new MemoryStream();
        document.Save(output);

        // Assert
        HeifFixtures.Create(kind).ShouldBe(expected);
        output.ToArray().ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Heif] - Corpus: ISO/IEC 23008-12 Exif removal and 14496-12 8.11.3 preserve every image extent")]
    public void RemoveExif_CheckedMetadataFixture_PreservesEveryImage()
    {
        // Arrange
        using var source = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestObjects", "Corpus", "metadata.bin"));
        using var document = HeifDocument.Load(source, new() { Strict = true });
        var images = new Dictionary<uint, byte[]>();
        foreach (var item in document.Items) { if (item.IsImage)
            {
                images.Add(item.Id, HeifFixtures.Data(item));
            }
        }

        // Act
        document.RemoveExif().ShouldBe(1);
        using var output = new MemoryStream();
        document.Save(output);
        output.Position = 0;
        using var reloaded = HeifDocument.Load(output, new() { Strict = true });

        // Assert
        reloaded.ExifItems.ShouldBeEmpty();
        foreach (var image in images)
        {
            HeifFixtures.Data(reloaded.GetItem(image.Key)!).ShouldBe(image.Value);
        }
    }
}
