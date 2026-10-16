using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Media;

namespace Assimalign.Cohesion.Content.Mpeg.Tests;

public sealed class Mp4GoldenTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Mpeg] - Golden writer provenance and preservation (ISO/IEC 14496-14 4, ISO/IEC 14496-12 8.5.2)")]
    [InlineData("progressive.mp4")][InlineData("fast-start.mp4")][InlineData("fragmented-cmaf.mp4")]
    [InlineData("audio-only.m4a")][InlineData("delayed-video.mp4")]
    public void Save_CheckedInGolden_ShouldMatchWriterAndRoundTrip(string name)
    {
        // Arrange
        var expected = File.ReadAllBytes(GoldenPath(name));
        expected.ShouldBe(Mp4GoldenFixtures.Create(name));
        using var source = new MemoryStream(expected);
        // Act
        using var file = Mp4File.Load(source);
        using var output = new MemoryStream();
        file.Document.Save(output);
        // Assert
        output.ToArray().ShouldBe(expected);
        file.Diagnostics.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Complete progressive/CMAF index equality (ISO/IEC 14496-12 8.6-8.8; ISO/IEC 23000-19 7.3)")]
    public async Task ReadSamples_ProgressiveAndCmafGoldens_ShouldProduceIdenticalIndexes()
    {
        // Arrange
        using var progressiveSource = File.OpenRead(GoldenPath("progressive.mp4"));
        using var fragmentedSource = File.OpenRead(GoldenPath("fragmented-cmaf.mp4"));
        using var progressive = Mp4File.Load(progressiveSource);
        using var fragmented = Mp4File.Load(fragmentedSource);
        var progressiveIndex = progressive.Tracks[0].Samples;
        var fragmentedIndex = fragmented.Tracks[0].Samples;
        var count = 0;
        // Act / Assert
        await foreach (var sample in fragmentedIndex.ReadSamplesAsync(CancellationToken.None))
        {
            var expected = (await progressiveIndex.GetSampleAsync(sample.Index, CancellationToken.None)).ShouldNotBeNull();
            sample.ShouldBe(expected);
            count++;
        }
        count.ShouldBe(4);
        progressive.Tracks.Count.ShouldBe(2);
        fragmented.Tracks.Count.ShouldBe(1); // A CMAF Track contains one track; the shared video content is identical.
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Golden edit delay and audio metadata (ISO/IEC 14496-12 8.6.6, 8.5.2.2)")]
    public async Task Seek_DelayedGolden_ShouldKeepAudioAndVideoTimelinesDistinct()
    {
        // Arrange
        using var source = File.OpenRead(GoldenPath("delayed-video.mp4"));
        using var file = Mp4File.Load(source);
        // Act
        var video = await file.Tracks[0].Samples.SeekAsync(new MediaTimestamp(1, 1), CancellationToken.None);
        var audio = await file.Tracks[1].Samples.SeekAsync(new MediaTimestamp(1, 1), CancellationToken.None);
        var delayed = (await file.Tracks[0].Samples.SeekAsync(new MediaTimestamp(3, 1), CancellationToken.None)).ShouldNotBeNull();
        // Assert
        video.ShouldBeNull();
        audio.ShouldNotBeNull();
        delayed.TargetSample.PresentationTime.ShouldBe(new MediaTimestamp(3, 1));
        delayed.TargetSample.DecodeTime.ShouldBe(new MediaTimestamp(1, 1));
        var audioTrack = file.Tracks[1].ShouldBeAssignableTo<IMediaAudioTrack>();
        audioTrack.ChannelCount.ShouldBe(2ul);
        audioTrack.SampleRate.ShouldBe(48000d);
    }

    private static string GoldenPath(string name) => Path.Combine(AppContext.BaseDirectory, "TestObjects", "Goldens", name);
}
