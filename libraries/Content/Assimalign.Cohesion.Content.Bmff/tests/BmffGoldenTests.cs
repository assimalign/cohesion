using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

public class BmffGoldenTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 clauses 8.2-8.12 golden files are byte-identical")]
    [InlineData("progressive")]
    [InlineData("fast-start")]
    [InlineData("fragmented")]
    [InlineData("heif")]
    public async Task Save_GoldenFile_IsByteIdentical(string fixture)
    {
        // Arrange
        var bytes = fixture switch { "progressive" => BmffGoldenFixtures.Progressive(false), "fast-start" => BmffGoldenFixtures.Progressive(true), "fragmented" => BmffGoldenFixtures.Fragmented(), _ => BmffGoldenFixtures.Heif() };
        using var source = new MemoryStream(bytes);
        using var document = BmffDocument.Load(source);
        using var output = new MemoryStream();
        // Act
        await document.SaveAsync(output, CancellationToken.None);
        // Assert
        output.ToArray().ShouldBe(bytes);
        foreach (var box in document.Boxes)
        {
            if (box is MediaDataBox media)
            {
                media.Payload.IsMaterialized.ShouldBeFalse();
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.7.3-5 fast start retains both tracks' samples")]
    public void Save_GoldenFastStart_LocatesIdenticalVideoAndAudioSamples()
    {
        // Arrange
        var original = BmffGoldenFixtures.Progressive(false);
        var relocated = BmffGoldenFixtures.Progressive(true);
        using var oldSource = new MemoryStream(original);
        using var newSource = new MemoryStream(relocated);
        using var oldDocument = BmffDocument.Load(oldSource);
        using var newDocument = BmffDocument.Load(newSource);
        // Act
        var oldSamples = Samples(oldDocument.Boxes, original);
        var newSamples = Samples(newDocument.Boxes, relocated);
        // Assert
        oldSamples.Count.ShouldBe(4);
        newSamples.Count.ShouldBe(4);
        for (var i = 0; i < oldSamples.Count; i++)
        {
            newSamples[i].ShouldBe(oldSamples[i]);
        }

        newDocument.Boxes[1].ShouldBeOfType<MovieBox>();
        newDocument.Boxes[2].ShouldBeOfType<MediaDataBox>();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.11.3 HEIF item extents track media growth")]
    public void Save_HeifMetadataGrowth_PreservesItemData()
    {
        // Arrange
        using var source = new MemoryStream(BmffGoldenFixtures.Heif());
        using var document = BmffDocument.Load(source);
        var meta = (MetaBox)document.Boxes[1];
        var location = (ItemLocationBox)meta.Children[3];
        var old = location.Entries[0].BaseOffset;
        meta.Children.Add(new XmlBox(0, 0) { Xml = "<annotation/>" });
        using var output = new MemoryStream();
        // Act
        document.Save(output);
        // Assert
        location.Entries[0].BaseOffset.ShouldBeGreaterThan(old);
        output.ToArray().AsSpan((int)location.Entries[0].BaseOffset, 6).ToArray().ShouldBe(new byte[] { 31, 32, 33, 34, 35, 36 });
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 8.8.8-12 fragmented indexes resolve both payloads")]
    public void Load_FragmentedGolden_ResolvesBothFragments()
    {
        // Arrange
        var bytes = BmffGoldenFixtures.Fragmented();
        using var source = new MemoryStream(bytes);
        using var document = BmffDocument.Load(source);
        var index = (TrackFragmentRandomAccessBox)((MovieFragmentRandomAccessBox)document.Boxes[6]).Children[0];
        // Act / Assert
        for (var i = 0; i < 2; i++)
        {
            var fragment = (MovieFragmentBox)document.Boxes[2 + i * 2];
            var track = (TrackFragmentBox)fragment.Children[1];
            var time = (TrackFragmentDecodeTimeBox)track.Children[1];
            var run = (TrackFragmentRunBox)track.Children[2];
            index.Entries[i].MoofOffset.ShouldBe((ulong)fragment.Offset);
            time.BaseMediaDecodeTime.ShouldBe((ulong)i * 6000);
            bytes.AsSpan((int)fragment.Offset + run.DataOffset!.Value, 6).ToArray().ShouldBe(
                i == 0 ? new byte[] { 1, 2, 3, 4, 5, 6 } : new byte[] { 7, 8, 9, 10, 11, 12 });
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - ISO/IEC 14496-12 4.2 and 8.5.2 unknown children retain every nesting level")]
    public void Load_UnknownsAtEveryLevel_PreservesOrderedChildren()
    {
        // Arrange
        using var source = new MemoryStream(BmffGoldenFixtures.Progressive(false));
        using var document = BmffDocument.Load(source);
        // Act / Assert
        var composites = CheckUnknowns((MovieBox)document.Boxes[2]);
        composites.ShouldBeGreaterThan(15);
        document.Boxes[3].ShouldBeOfType<UnknownBox>();
    }

    private static int CheckUnknowns(BmffBoxComposite parent)
    {
        parent.Children[parent.Children.Count - 1].ShouldBeOfType<UnknownBox>().BoxType.ToFourCC().ToString().ShouldBe("zzzz");
        var count = 1;
        foreach (var child in parent.Children)
        {
            if (child is BmffBoxComposite composite)
            {
                count += CheckUnknowns(composite);
            }
        }
        return count;
    }

    private static List<byte[]> Samples(IList<BmffBox> boxes, byte[] file)
    {
        var samples = new List<byte[]>();
        foreach (var box in boxes)
        {
            if (box is ChunkOffsetBox chunks)
            {
                foreach (var offset in chunks.Entries)
                {
                    samples.Add(file.AsSpan((int)offset, 3).ToArray());
                }
            }
            if (box is BmffBoxComposite composite)
            {
                samples.AddRange(Samples(composite.Children, file));
            }
        }
        return samples;
    }
}
