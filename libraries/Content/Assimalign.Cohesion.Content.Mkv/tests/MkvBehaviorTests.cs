using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Media;

namespace Assimalign.Cohesion.Content.Mkv.Tests;

public sealed class MkvBehaviorTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §§5.1.4,11: Tracks use lazy bytes and nanoseconds")]
    public async Task Load_WebM_MapsTracksAndTimes()
    {
        // Arrange
        using var source = new MemoryStream(MkvFixtures.Create());
        using var document = MkvDocument.Load(source);

        // Act
        var video = (IMediaVideoTrack)document.Tracks[0];
        var audio = (IMediaAudioTrack)document.Tracks[1];
        var sample = await audio.Samples.GetSampleAsync(0, CancellationToken.None);

        // Assert
        video.PixelWidth.ShouldBe(640UL);
        video.DisplayAspectRatio.ShouldBe((640UL, 360UL));
        audio.SampleRate.ShouldBe(48_000);
        audio.CodecPrivateData.IsMaterialized.ShouldBeFalse();
        audio.Language.ShouldBe("eng");
        sample!.Value.PresentationTime.ShouldBe(new MediaTimestamp(-6_500_000, 1_000_000_000));
        sample.Value.DecodeTime.ShouldBeNull();
        document.Duration.ShouldBe(new MediaTimestamp(2500, 1000));
    }

    [Theory(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §10.3: All laces retain lazy frame boundaries")]
    [InlineData(MkvLacing.Xiph, 2, 3, 1)]
    [InlineData(MkvLacing.FixedSize, 2, 2, 2)]
    [InlineData(MkvLacing.Ebml, 2, 3, 1)]
    public void Parse_LacedBlock_RetainsFrames(MkvLacing mode, long first, long second, long last)
    {
        // Arrange
        using var source = new MemoryStream(MkvFixtures.Laced(mode));
        var bytes = BinarySegment.Create(source, 0, source.Length);

        // Act
        var block = MkvBlock.Parse(bytes, 100);

        // Assert
        block.Lacing.ShouldBe(mode); block.TrackNumber.ShouldBe(2UL);
        block.Frames.Count.ShouldBe(3);
        block.Frames[0].Length.ShouldBe(first); block.Frames[1].Length.ShouldBe(second); block.Frames[2].Length.ShouldBe(last);
        block.Frames[0].IsMaterialized.ShouldBeFalse();
        block.FrameOffsets[2].ShouldBe(100 + source.Length - last);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §§6.4,10.4: Cues and cluster scan agree on keyframe")]
    public async Task Seek_WithAndWithoutCues_ReturnsSameKeyframe()
    {
        // Arrange
        using var first = new MemoryStream(MkvFixtures.Create());
        using var second = new MemoryStream(MkvFixtures.Create(cues: false));
        using var cues = MkvDocument.Load(first);
        using var scan = MkvDocument.Load(second);
        var time = new MediaTimestamp(1750, 1000);

        // Act
        var fromCues = await cues.Tracks[0].Samples.SeekAsync(time, CancellationToken.None);
        var fromScan = await scan.Tracks[0].Samples.SeekAsync(time, CancellationToken.None);

        // Assert
        fromCues!.Value.SyncSample.PresentationTime.ShouldBe(new MediaTimestamp(1, 1));
        fromCues.Value.SyncSample.Index.ShouldBe(fromScan!.Value.SyncSample.Index);
        fromCues.Value.TargetSample.Index.ShouldBe(3);
        fromCues.Value.ByteOffset.ShouldBeLessThan(fromCues.Value.SyncSample.ByteOffset);
        (fromCues.Value.ByteOffset + fromCues.Value.ByteLength).ShouldBeGreaterThan(fromCues.Value.TargetSample.ByteOffset);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 9559 §§8,16: Metadata edits relocate indexes without changing clusters")]
    public async Task Save_MetadataEdits_PreservesClustersAndSeekTargets()
    {
        // Arrange
        var bytes = MkvFixtures.Create(webm: false, metadata: true);
        using var source = new MemoryStream(bytes);
        using var document = MkvDocument.Load(source);
        using var unchanged = new MemoryStream();
        document.Save(unchanged);
        unchanged.ToArray().ShouldBe(bytes);
        var tracks = MkvFixtures.Find(document.Segment, 0x1654AE6B);
        MkvFixtures.Find(tracks.Children[0], 0x22B59C).Value = "fra";
        var tags = MkvFixtures.Find(document.Segment, 0x1254C367);
        MkvFixtures.Find(MkvFixtures.Find(tags.Children[0], 0x67C8), 0x4487).Value = new string('x', 500);

        // Act
        using var output = new MemoryStream();
        document.Save(output); output.Position = 0;
        using var reloaded = MkvDocument.Load(output);
        var seek = await reloaded.Tracks[0].Samples.SeekAsync(new MediaTimestamp(1750, 1000), CancellationToken.None);

        // Assert
        reloaded.Tracks[0].Language.ShouldBe("fra");
        seek!.Value.SyncSample.PresentationTime.ShouldBe(new MediaTimestamp(1, 1));
        var originalBlock = (BinarySegment)MkvFixtures.Find(MkvFixtures.Find(document.Segment, 0x1F43B675), 0xA3).Value!;
        var savedBlock = (BinarySegment)MkvFixtures.Find(MkvFixtures.Find(reloaded.Segment, 0x1F43B675), 0xA3).Value!;
        savedBlock.ToArray(100).ShouldBe(originalBlock.ToArray(100));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mkv] - RFC 8794 §6.2: Live unknown masters expose a finite snapshot")]
    public async Task Load_LiveUnknownSizes_ReportsSnapshotCapabilities()
    {
        // Arrange
        using var source = new MemoryStream(MkvFixtures.Create(cues: false, live: true));
        using var document = MkvDocument.Load(source);

        // Act
        var sample = await document.Tracks[0].Samples.FindSampleAsync(new MediaTimestamp(900, 1), CancellationToken.None);

        // Assert
        document.IsLive.ShouldBeTrue();
        document.Tracks[0].Samples.Count.ShouldBeNull();
        sample!.Value.Index.ShouldBe(5);
    }
}
