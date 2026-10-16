using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Media;

namespace Assimalign.Cohesion.Content.Mpeg.Tests;

public sealed class Mp4BoundaryTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Previous traf base and defaults precedence (ISO/IEC 14496-12 8.8.3, 8.8.7-8.8.8)")]
    public async Task GetSample_ImplicitPreviousTraf_ShouldResolveDefaultsAndContinuity()
    {
        // Arrange
        using var source = new MemoryStream(Mp4FragmentFixtures.ImplicitPreviousTrack());
        using var file = Mp4File.Load(source);
        // Act
        var videoFirst = (await file.Tracks[0].Samples.GetSampleAsync(0, CancellationToken.None)).ShouldNotBeNull();
        var videoLast = (await file.Tracks[0].Samples.GetSampleAsync(1, CancellationToken.None)).ShouldNotBeNull();
        var audioFirst = (await file.Tracks[1].Samples.GetSampleAsync(0, CancellationToken.None)).ShouldNotBeNull();
        // Assert
        videoFirst.IsSync.ShouldBeFalse();
        videoLast.IsSync.ShouldBeTrue();
        videoLast.DecodeTime.ShouldBe(new MediaTimestamp(1, 1));
        videoLast.ByteLength.ShouldBe(3);
        audioFirst.ByteOffset.ShouldBe(videoLast.ByteOffset + 3);
        source.Position = audioFirst.ByteOffset;
        source.ReadByte().ShouldBe(21);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Variable runs, ties and chunk transitions (ISO/IEC 14496-12 8.6.1, 8.7.3-8.7.5)")]
    public async Task ReadSamples_VariableTables_ShouldMatchIndependentVectors()
    {
        // Arrange
        var bytes = Mp4Fixtures.Mutate(Mp4MediaFixtures.Progressive(), document =>
        {
            var movie = (MovieBox)document.Boxes[2];
            Mp4MediaFixtures.Find<MovieHeaderBox>(movie).Duration = 0;
            Mp4MediaFixtures.Find<TrackHeaderBox>(movie).Duration = 0;
            var size = Mp4MediaFixtures.Find<SampleSizeBox>(movie);
            size.SampleSize = 0;
            size.Entries = [1, 2, 4, 5];
            Mp4MediaFixtures.Find<SampleDecodingTimeBox>(movie).Entries =
                [new() { SampleCount = 2, SampleDelta = 10 }, new() { SampleCount = 2, SampleDelta = 20 }];
            var chunks = Mp4MediaFixtures.Find<SampleToChunkBox>(movie);
            chunks.Entries = [new() { FirstChunk = 1, SamplesPerChunk = 2, SampleDescriptionIndex = 1 }];
            var offsets = Mp4MediaFixtures.Find<ChunkOffsetBox>(movie);
            offsets.Entries = [offsets.Entries[0], offsets.Entries[0] + 3];
            var table = Mp4MediaFixtures.Find<SampleTableBox>(movie);
            table.Children.Add(new SampleCompositionTimeBox(0, 0)
            {
                Version = 1, Entries = [new() { SampleCount = 1, SampleOffset = 5 }, new() { SampleCount = 2, SampleOffset = -5 }, new() { SampleCount = 1 }]
            });
            Mp4MediaFixtures.Find<SyncSampleTableBox>(movie).Entries = [1, 4];
        });
        using var source = new MemoryStream(bytes);
        using var file = Mp4File.Load(source);
        var samples = file.Tracks[0].Samples;
        long[] expectedDts = [0, 10, 20, 40];
        long[] expectedPts = [5, 5, 15, 40];
        long[] expectedSizes = [1, 2, 4, 5];
        var ordinal = 0;

        // Act / Assert
        await foreach (var sample in samples.ReadSamplesAsync(CancellationToken.None))
        {
            sample.DecodeTime.ShouldBe(new MediaTimestamp(expectedDts[ordinal], 1000));
            sample.PresentationTime.ShouldBe(new MediaTimestamp(expectedPts[ordinal], 1000));
            sample.ByteLength.ShouldBe(expectedSizes[ordinal]);
            sample.ShouldBe((await samples.GetSampleAsync(ordinal, CancellationToken.None)).ShouldNotBeNull());
            ordinal++;
        }
        (await samples.FindSampleAsync(new MediaTimestamp(5, 1000), CancellationToken.None)).ShouldNotBeNull().Index.ShouldBe(0);
        (await samples.SeekAsync(new MediaTimestamp(30, 1000), CancellationToken.None)).ShouldNotBeNull().ByteLength.ShouldBe(7);
        file.Duration.ShouldBe(new MediaTimestamp(60, 1000));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Resolved fragment presentation extent (ISO/IEC 14496-12 8.8.8)")]
    public void Load_CompositionExtendsFragment_ShouldIncludePresentationTail()
    {
        // Arrange
        var bytes = Mp4Fixtures.Mutate(Mp4MediaFixtures.Fragmented(), document =>
        {
            var moof = (MovieFragmentBox)document.Boxes[4];
            var run = Mp4MediaFixtures.Find<TrackFragmentRunBox>(moof);
            run.Flags |= 0x800;
            run.Entries[1].CompositionTimeOffset = 2000;
            run.DataOffset += 8; // Two newly encoded composition offsets enlarge this moof.
        });
        using var source = new MemoryStream(bytes);
        // Act
        using var file = Mp4File.Load(source);
        // Assert
        file.Duration.ShouldBe(new MediaTimestamp(6, 1));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - 40 GiB media remains lazy (ISO/IEC 14496-12 4.2, 8.7.5)")]
    public async Task Load_LargeSparseMdat_ShouldUse64BitRangesWithoutPayloadReads()
    {
        // Arrange
        const long payloadLength = 40L * 1024 * 1024 * 1024;
        var brand = Mp4Fixtures.Write(Mp4Fixtures.Brand("mp42"));
        using var header = new MemoryStream();
        header.Write(brand);
        BmffBoxHeader.Create(BmffBoxType.MediaData, payloadLength).Write(header);
        var payloadEnd = header.Length + payloadLength;
        var track = Mp4MediaFixtures.Track(1, true, 0, count: 1, wide: true);
        Mp4MediaFixtures.Find<ChunkOffset64BitBox>(track).Entries = [(ulong)(payloadEnd - 3)];
        var movie = Mp4Fixtures.Container(new MovieBox(0, 0), new MovieHeaderBox(0, 0), track);
        using var source = new Mp4SparseStream(header.ToArray(), Mp4Fixtures.Write(movie), payloadEnd);
        // Act
        using var file = Mp4File.Load(source);
        var sample = (await file.Tracks[0].Samples.GetSampleAsync(0, CancellationToken.None)).ShouldNotBeNull();
        // Assert
        sample.ByteOffset.ShouldBe(payloadEnd - 3);
        sample.ByteLength.ShouldBe(3);
        source.BytesRead.ShouldBeLessThan(16384);
        Mp4Profile.FastStart(file.Document);
        source.BytesRead.ShouldBeLessThan(16384);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Forward-only access uses BMFF reader and writer (ISO/IEC 14496-12 4.2)")]
    public void Read_NonSeekablePartialDelivery_ShouldSkipWithoutBufferingMedia()
    {
        // Arrange
        using var storage = new MemoryStream();
        using var output = new Mp4ForwardStream(storage);
        using (var writer = BmffWriter.Create(output))
        {
            writer.Write(Mp4Fixtures.Brand("mp42"));
            writer.Write(new MediaDataBox(0, 0) { Payload = BinarySegment.Create(new byte[4096]) });
            writer.Write(new Avc1SampleEntryBox { Width = 16, Height = 16 });
        }
        storage.Position = 0;
        using var input = new Mp4ForwardStream(storage);
        using var reader = BmffReader.Create(input, null, Mp4Profile.CreateRegistry());
        // Act / Assert
        reader.Read().ShouldBeTrue();
        reader.Current.ShouldBeOfType<FileTypeBox>();
        reader.Read().ShouldBeTrue();
        reader.Header.BoxType.ShouldBe(BmffBoxType.MediaData);
        reader.Skip();
        reader.Read().ShouldBeTrue();
        reader.Current.ShouldBeOfType<Avc1SampleEntryBox>().Width.ShouldBe((ushort)16);
        reader.Read().ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - External data references refuse single-source ranges (ISO/IEC 14496-12 8.7.2)")]
    public async Task Seek_ExternalDataReference_ShouldReportUnsupported()
    {
        // Arrange
        var bytes = Mp4Fixtures.Mutate(Mp4MediaFixtures.Progressive(), document =>
        {
            var url = Mp4MediaFixtures.Find<DataEntryUrlBox>((MovieBox)document.Boxes[2]);
            url.Flags = 0;
            url.Location = "https://example.invalid/media";
        });
        using var source = new MemoryStream(bytes);
        using var file = Mp4File.Load(source);
        // Act / Assert
        file.Tracks[0].Samples.CanSeek.ShouldBeFalse();
        await Should.ThrowAsync<NotSupportedException>(async () =>
            await file.Tracks[0].Samples.SeekAsync(new MediaTimestamp(1, 1), CancellationToken.None));
        Should.Throw<ContentFormatException>(() => Mp4Profile.FastStart(file.Document)).ErrorCode.ShouldBe(ContentFormatErrorCode.Unsupported);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Positive media-time edit preserves decode preroll (ISO/IEC 14496-12 8.6.6)")]
    public async Task Seek_TrimmedMediaWithEmptyEdit_ShouldIncludePrerollBytes()
    {
        // Arrange
        var bytes = Mp4Fixtures.Mutate(Mp4MediaFixtures.Progressive(delay: true), document =>
        {
            var edits = Mp4MediaFixtures.Find<EditListBox>((MovieBox)document.Boxes[2]);
            edits.Entries[1].MediaTime = 1000;
            edits.Entries[1].SegmentDuration = 2000;
        });
        using var source = new MemoryStream(bytes);
        using var file = Mp4File.Load(source);
        var index = file.Tracks[0].Samples;
        // Act
        var before = await index.FindSampleAsync(new MediaTimestamp(1999, 1000), CancellationToken.None);
        var seek = (await index.SeekAsync(new MediaTimestamp(2, 1), CancellationToken.None)).ShouldNotBeNull();
        // Assert
        before.ShouldBeNull();
        seek.TargetSample.Index.ShouldBe(1);
        seek.SyncSample.Index.ShouldBe(0);
        seek.ByteLength.ShouldBe(6);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Fragment sync range includes moof framing (ISO/IEC 14496-12 8.8)")]
    public async Task Seek_FragmentedSample_ShouldCoverHeaderAndDecodeInterval()
    {
        // Arrange
        using var source = new MemoryStream(Mp4MediaFixtures.Fragmented());
        using var file = Mp4File.Load(source);
        // Act
        var seek = (await file.Tracks[0].Samples.SeekAsync(new MediaTimestamp(3, 1), CancellationToken.None)).ShouldNotBeNull();
        // Assert
        seek.SyncSample.Index.ShouldBe(2);
        seek.TargetSample.Index.ShouldBe(3);
        seek.ByteOffset.ShouldBe(file.Document.Boxes[4].Offset);
        (seek.ByteOffset + seek.ByteLength).ShouldBe(seek.TargetSample.ByteOffset + 3);
    }
}
