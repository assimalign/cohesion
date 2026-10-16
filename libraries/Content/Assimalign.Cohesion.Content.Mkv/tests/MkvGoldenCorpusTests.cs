using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml;
using Assimalign.Cohesion.Content.Media;

namespace Assimalign.Cohesion.Content.Mkv.Tests;

// Each checked-in Golden file is exactly MkvFixtures.Create with the arguments below.
// These are specification-shaped containers emitted by our writer, with placeholder codec bytes.
public sealed class MkvGoldenCorpusTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §8 and RFC 8794 §8: Golden containers round-trip exactly")]
    [InlineData("vp9-opus.webm", true, true, false, false, false)]
    [InlineData("metadata.mkv", false, true, false, true, false)]
    [InlineData("live.webm", true, false, true, false, false)]
    [InlineData("laces.mkv", false, true, false, false, true)]
    public void Save_GoldenFile_PreservesEveryByte(string file, bool webm, bool cues, bool live, bool metadata, bool laces)
    {
        // Arrange
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestObjects", "Golden", file));
        bytes.ShouldBe(MkvFixtures.Create(webm, cues, live, metadata, laces));
        using var source = new MemoryStream(bytes);
        using var document = MkvDocument.Load(source);

        // Act
        using var saved = new MemoryStream(); document.Save(saved);

        // Assert
        saved.ToArray().ShouldBe(bytes);
        document.Tracks.Count.ShouldBe(metadata ? 3 : 2);
        if (metadata)
        {
            document.Tracks[2].Kind.ShouldBe(MediaTrackKind.Text);
            document.ReadAttachmentData(MkvFixtures.Find(document.Segment, 0x1941A469).Children[0]).ShouldBe(new byte[] { 10, 20, 30, 40 });
            MkvFixtures.Find(document.Segment, 0x1043A770).ShouldNotBeNull();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §§6.3,6.4,8,16: Repeated metadata saves retain all cluster bytes and indexes")]
    public async Task Save_RepeatedGrowthAndShrink_PreservesWholeClustersAndPositions()
    {
        // Arrange
        using var source = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestObjects", "Golden", "metadata.mkv"));
        using var document = MkvDocument.Load(source);
        var originalClusters = ClusterBytes(document, source);
        var tag = MkvFixtures.Find(MkvFixtures.Find(MkvFixtures.Find(document.Segment, 0x1254C367).Children[0], 0x67C8), 0x4487);
        var track = MkvFixtures.Find(document.Segment, 0x1654AE6B).Children[0];
        MkvFixtures.Find(track, 0x22B59C).Value = "fra";

        foreach (var length in new[] { 1000, 1, 18, 1000 })
        {
            // Act
            tag.Value = new string('x', length);
            using var output = new MemoryStream(); document.Save(output); output.Position = 0;
            using var loaded = MkvDocument.Load(output);
            var result = await loaded.Tracks[0].Samples.SeekAsync(new MediaTimestamp(1750, 1000), CancellationToken.None);

            // Assert
            loaded.Tracks[0].Language.ShouldBe("fra");
            result!.Value.SyncSample.Index.ShouldBe(2);
            result.Value.SyncSample.PresentationTime.ShouldBe(new MediaTimestamp(1, 1));
            var savedClusters = ClusterBytes(loaded, output);
            savedClusters.Count.ShouldBe(originalClusters.Count);
            for (var i = 0; i < savedClusters.Count; i++) { savedClusters[i].ShouldBe(originalClusters[i]); }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §§10.3.2-10.3.5,11: Golden laces have distinct exact frame times")]
    public async Task ReadSamples_GoldenLaces_ExpandsNineFrames()
    {
        // Arrange
        using var source = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "TestObjects", "Golden", "laces.mkv"));
        using var document = MkvDocument.Load(source);
        var samples = new List<MediaSample>();

        // Act
        await foreach (var sample in document.Tracks[1].Samples.ReadSamplesAsync(CancellationToken.None)) { samples.Add(sample); }

        // Assert
        samples.Count.ShouldBe(9);
        samples[0].PresentationTime.ShouldBe(new MediaTimestamp(-6_500_000, 1_000_000_000));
        samples[1].PresentationTime.ShouldBe(new MediaTimestamp(13_500_000, 1_000_000_000));
        samples[2].PresentationTime.ShouldBe(new MediaTimestamp(33_500_000, 1_000_000_000));
        samples[8].Index.ShouldBe(8);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §11: Deprecated TrackTimestampScale is reported and ignored")]
    public async Task ReadSamples_DeprecatedTrackScale_ReportsAndIgnores()
    {
        // Arrange
        var bytes = MkvMalformedTests.Rewrite(document => MkvFixtures.Find(document.Segment, 0x1654AE6B).Children[0].Children.Add(
            EbmlElement.Create(MkvFixtures.Id(0x23314F), EbmlElementType.Float, 2.0)));
        using var source = new MemoryStream(bytes);
        using var document = MkvDocument.Load(source);

        // Act
        var sample = await document.Tracks[0].Samples.GetSampleAsync(2, CancellationToken.None);

        // Assert
        sample!.Value.PresentationTime.ShouldBe(new MediaTimestamp(1, 1));
        document.Diagnostics.Count.ShouldBe(1);
        document.Diagnostics[0].ErrorCode.ShouldBe(ContentFormatErrorCode.Unsupported);
        document.Diagnostics[0].Position.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §10.3.5: Underdetermined lace timing is explicitly unsupported")]
    public async Task ReadSamples_LaceWithoutDefaultDuration_DoesNotInventTimestamps()
    {
        // Arrange
        var bytes = MkvMalformedTests.Rewrite(document =>
        {
            var audio = MkvFixtures.Find(document.Segment, 0x1654AE6B).Children[1];
            audio.Children.Remove(MkvFixtures.Find(audio, 0x23E383));
            var cluster = MkvFixtures.Find(document.Segment, 0x1F43B675);
            foreach (var child in cluster.Children)
            { if (child.Identifier.EncodedValue == 0xA3 && ((BinarySegment)child.Value!).ToArray(64)[0] == 0x82) { child.Value = BinarySegment.Create(MkvFixtures.Laced(MkvLacing.FixedSize)); } }
        });
        using var source = new MemoryStream(bytes);
        using var document = MkvDocument.Load(source);

        // Act / Assert
        await Should.ThrowAsync<NotSupportedException>(() => document.Tracks[1].Samples.GetSampleAsync(1, CancellationToken.None).AsTask());
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §§5.1.4,14: ContentEncodings stay opaque and disable safe range claims")]
    public void Load_ContentEncodings_PreservesUnknownChildren()
    {
        // Arrange
        var bytes = MkvMalformedTests.Rewrite(document =>
        {
            var encodings = EbmlElement.Create(MkvFixtures.Id(0x6D80), EbmlElementType.MasterElement);
            encodings.Children.Add(EbmlElement.Create(MkvFixtures.Id(0x6240), EbmlElementType.Binary, BinarySegment.Create(new byte[] { 1, 2, 3 })));
            MkvFixtures.Find(document.Segment, 0x1654AE6B).Children[0].Children.Add(encodings);
            var video = MkvFixtures.Find(MkvFixtures.Find(document.Segment, 0x1654AE6B).Children[0], 0xE0);
            var colour = EbmlElement.Create(MkvFixtures.Id(0x55B0), EbmlElementType.MasterElement);
            colour.Children.Add(EbmlElement.Create(MkvFixtures.Id(0x55B1), EbmlElementType.Binary, BinarySegment.Create(new byte[] { 9 })));
            video.Children.Add(colour);
        });
        using var source = new MemoryStream(bytes);
        using var document = MkvDocument.Load(source);

        // Act
        using var output = new MemoryStream(); document.Save(output);

        // Assert
        document.Tracks[0].Samples.CanSeek.ShouldBeFalse();
        output.ToArray().ShouldBe(bytes);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §4.4: Edited nonzero default values use nonempty payloads")]
    public void Save_NonzeroDefault_EmitsExplicitValue()
    {
        // Arrange
        using var source = new MemoryStream(MkvFixtures.Create(cues: false));
        using var document = MkvDocument.Load(source);
        var entry = MkvFixtures.Find(document.Segment, 0x1654AE6B).Children[0];
        entry.Children.Add(EbmlElement.Create(MkvFixtures.Id(0xB9), EbmlElementType.UnsignedInteger, 1UL));
        using var output = new MemoryStream(); document.Save(output); output.Position = 0;
        using var reloaded = MkvDocument.Load(output);

        // Act
        var enabled = MkvFixtures.Find(MkvFixtures.Find(reloaded.Segment, 0x1654AE6B).Children[0], 0xB9);
        var data = output.ToArray();

        // Assert
        data[enabled.Offset + 1].ShouldBe((byte)0x81);
        data[enabled.Offset + 2].ShouldBe((byte)1);
    }

    private static List<byte[]> ClusterBytes(MkvDocument document, Stream source)
    {
        var result = new List<byte[]>();
        for (var i = 0; i < document.Segment.Children.Count; i++)
        {
            var element = document.Segment.Children[i];
            if (element.Identifier.EncodedValue != 0x1F43B675) { continue; }
            var end = i + 1 < document.Segment.Children.Count ? document.Segment.Children[i + 1].Offset : source.Length;
            result.Add(BinarySegment.Create(source, element.Offset, end - element.Offset).ToArray(16_384));
        }
        return result;
    }
}
