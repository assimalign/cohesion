using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Media;

namespace Assimalign.Cohesion.Content.Mpeg.Tests;

[Collection("MP4 allocation")]
public sealed class Mp4SampleTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Mpeg] - Progressive table walk (ISO/IEC 14496-12 8.6.1, 8.7.3-8.7.5)")]
    [InlineData(false, false)][InlineData(true, false)][InlineData(false, true)][InlineData(true, true)]
    public async Task GetSample_ProgressiveTables_ShouldResolveBytesAndTimes(bool compact, bool wide)
    {
        // Arrange
        using var source = new MemoryStream(Mp4MediaFixtures.Progressive(compact: compact, wide: wide));
        using var file = Mp4File.Load(source);
        var track = file.Tracks[0].ShouldBeAssignableTo<IMediaVideoTrack>();

        // Act
        var sample = (await track.Samples.GetSampleAsync(3, CancellationToken.None)).ShouldNotBeNull();

        // Assert
        track.Language.ShouldBe("en");
        track.PixelWidth.ShouldBe(1920ul);
        track.PixelHeight.ShouldBe(1080ul);
        track.CodecPrivateData.IsMaterialized.ShouldBeFalse();
        sample.DecodeTime.ShouldBe(new MediaTimestamp(3000, 1000));
        sample.PresentationTime.ShouldBe(new MediaTimestamp(3, 1));
        sample.IsSync.ShouldBeFalse();
        source.Position = sample.ByteOffset;
        source.ReadByte().ShouldBe(10);
        (await track.Samples.GetSampleAsync(4, CancellationToken.None)).ShouldBeNull();
        track.Samples.Count.ShouldBe(4);
        file.Duration.ShouldBe(new MediaTimestamp(4, 1));
    }

    [Theory(DisplayName = "Cohesion Test [Content.Mpeg] - Fragment defaults and data bases (ISO/IEC 14496-12 8.8.3, 8.8.7-8.8.8, 8.8.12)")]
    [InlineData(true, false, false)][InlineData(false, false, false)][InlineData(true, true, false)][InlineData(true, false, true)]
    public async Task ReadSamples_FragmentedFile_ShouldMatchProgressiveSemantics(bool defaults, bool implicitBase, bool explicitBase)
    {
        // Arrange
        using var progressiveSource = new MemoryStream(Mp4MediaFixtures.Progressive());
        using var fragmentedSource = new MemoryStream(Mp4MediaFixtures.Fragmented(defaults, implicitBase, explicitBase));
        using var progressive = Mp4File.Load(progressiveSource);
        using var fragmented = Mp4File.Load(fragmentedSource);
        var index = 0L;

        // Act / Assert
        await foreach (var sample in fragmented.Tracks[0].Samples.ReadSamplesAsync(CancellationToken.None))
        {
            var expected = (await progressive.Tracks[0].Samples.GetSampleAsync(index++, CancellationToken.None)).ShouldNotBeNull();
            sample.Index.ShouldBe(expected.Index);
            sample.TrackId.ShouldBe(expected.TrackId);
            sample.DecodeTime.ShouldBe(expected.DecodeTime);
            sample.PresentationTime.ShouldBe(expected.PresentationTime);
            sample.ByteLength.ShouldBe(expected.ByteLength);
            sample.IsSync.ShouldBe(expected.IsSync);
            fragmentedSource.Position = sample.ByteOffset;
            progressiveSource.Position = expected.ByteOffset;
            fragmentedSource.ReadByte().ShouldBe(progressiveSource.ReadByte());
        }
        index.ShouldBe(4);
        fragmented.IsFragmented.ShouldBeTrue();
        fragmented.Diagnostics.ShouldBeEmpty();
        fragmented.Duration.ShouldBe(new MediaTimestamp(4, 1));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Empty edit delays presentation and seek (ISO/IEC 14496-12 8.6.6)")]
    public async Task Seek_EmptyEdit_ShouldHonorVideoDelay()
    {
        // Arrange
        using var source = new MemoryStream(Mp4MediaFixtures.Progressive(delay: true));
        using var file = Mp4File.Load(source);
        var index = file.Tracks[0].Samples;
        // Act
        var before = await index.SeekAsync(new MediaTimestamp(1999, 1000), CancellationToken.None);
        var seek = (await index.SeekAsync(new MediaTimestamp(3500, 1000), CancellationToken.None)).ShouldNotBeNull();
        // Assert
        before.ShouldBeNull();
        seek.SyncSample.Index.ShouldBe(0);
        seek.TargetSample.Index.ShouldBe(1);
        seek.SyncSample.PresentationTime.ShouldBe(new MediaTimestamp(2, 1));
        seek.ByteLength.ShouldBe(6);
        file.Tracks[0].Duration.ShouldBe(new MediaTimestamp(6, 1));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Signed composition offsets and decode coverage (ISO/IEC 14496-12 8.6.1.3, 8.6.2)")]
    public async Task Seek_ReorderedPresentation_ShouldCoverIntermediateSamples()
    {
        // Arrange
        var bytes = Mp4Fixtures.Mutate(Mp4MediaFixtures.Progressive(reorder: true), document =>
            Mp4MediaFixtures.Find<SyncSampleTableBox>((MovieBox)document.Boxes[2]).Entries = [1]);
        using var source = new MemoryStream(bytes);
        using var file = Mp4File.Load(source);
        // Act
        var result = (await file.Tracks[0].Samples.SeekAsync(new MediaTimestamp(1, 1), CancellationToken.None)).ShouldNotBeNull();
        // Assert
        result.TargetSample.Index.ShouldBe(2);
        result.TargetSample.DecodeTime.ShouldBe(new MediaTimestamp(2, 1));
        result.SyncSample.Index.ShouldBe(0);
        result.ByteLength.ShouldBe(9);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Fast start relocates sample offsets (ISO/IEC 14496-12 8.7.5)")]
    public async Task FastStart_ProgressiveMovie_ShouldKeepSampleBytes()
    {
        // Arrange
        using var source = new MemoryStream(Mp4MediaFixtures.Progressive());
        using var document = Mp4Profile.LoadDocument(source);
        // Act
        Mp4Profile.FastStart(document);
        using var output = new MemoryStream();
        document.Save(output);
        output.Position = 0;
        using var file = Mp4File.Load(output);
        var sample = (await file.Tracks[0].Samples.GetSampleAsync(3, CancellationToken.None)).ShouldNotBeNull();
        // Assert
        file.Document.Boxes[1].ShouldBeOfType<MovieBox>();
        output.Position = sample.ByteOffset;
        output.ReadByte().ShouldBe(10);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Native index bounded allocation (ISO/IEC 14496-12 8.6.1, 8.7.3-8.7.5)")]
    public async Task GetSample_MillionConstantSizeSamples_ShouldUseBoundedMemory()
    {
        // Arrange
        var smallBytes = Mp4MediaFixtures.Progressive(samples: 64);
        var largeBytes = Mp4MediaFixtures.Progressive(samples: 1_000_000);
        using var warmSource = new MemoryStream(smallBytes);
        using var warm = Mp4File.Load(warmSource);
        using var smallSource = new MemoryStream(smallBytes);
        using var largeSource = new MemoryStream(largeBytes);
        var beforeSmall = GC.GetAllocatedBytesForCurrentThread();
        using var small = Mp4File.Load(smallSource);
        var smallBytesAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeSmall;
        var beforeLarge = GC.GetAllocatedBytesForCurrentThread();
        using var large = Mp4File.Load(largeSource);
        var largeBytesAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeLarge;
        var index = large.Tracks[0].Samples;
        _ = await index.GetSampleAsync(999999, CancellationToken.None);
        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1024; i++) { _ = await index.GetSampleAsync(999999, CancellationToken.None); }
        var queryBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        // Assert
        largeBytesAllocated.ShouldBeLessThanOrEqualTo(smallBytesAllocated + 4096);
        queryBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Cancellation and borrowed source (ISO/IEC 14496-12 8.1)")]
    public async Task LoadAsync_CancellationAndOwnership_ShouldHonorCaller()
    {
        // Arrange
        using var source = new MemoryStream(Mp4MediaFixtures.Progressive());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await Mp4File.LoadAsync(source, cancellationToken: cancellation.Token));
        source.Position = 0;
        var file = await Mp4File.LoadAsync(source, cancellationToken: CancellationToken.None);
        await file.DisposeAsync();
        source.CanRead.ShouldBeTrue();
    }
}
