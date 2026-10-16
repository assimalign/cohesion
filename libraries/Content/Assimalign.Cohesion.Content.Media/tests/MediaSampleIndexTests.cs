using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Content.Media.Tests;

public class MediaSampleIndexTests
{
    private readonly ITestOutputHelper _output;

    public MediaSampleIndexTests(ITestOutputHelper output) => _output = output;

    [Theory(DisplayName = "Cohesion Test [Content.Media] - Random lookup allocation stays bounded for a million-sample track")]
    [InlineData(64)]
    [InlineData(1000000)]
    public async Task GetSampleAsync_GeneratedTrack_KeepsConstructionAndLookupAllocationBounded(long count)
    {
        // Arrange: warm JIT paths before measuring thread-local allocation on synchronously completed ValueTasks.
        const int iterations = 4096;
        var warm = new GeneratedMediaSampleIndex(64);
        for (var i = 0; i < 256; i++)
        {
            _ = await warm.GetSampleAsync(i % 64, CancellationToken.None);
            _ = await warm.FindSampleAsync(new MediaTimestamp(i * 1001L, 30000), CancellationToken.None);
            _ = await warm.SeekAsync(new MediaTimestamp(i * 1001L, 30000), CancellationToken.None);
        }
        var beforeConstruction = GC.GetAllocatedBytesForCurrentThread();
        var index = new GeneratedMediaSampleIndex(count);
        IMediaTrack track = new TestMediaTrack(MediaTrackKind.Video, index);
        var constructionBytes = GC.GetAllocatedBytesForCurrentThread() - beforeConstruction;
        long checksum = 0;
        var allSynchronous = true;
        var measuringThread = Environment.CurrentManagedThreadId;

        // Act: varying requests near both ends and throughout the logical track; no async continuation changes threads.
        var beforeLookup = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            var number = (i * 7919L) % count;
            var indexLookup = track.Samples.GetSampleAsync(number, CancellationToken.None);
            allSynchronous &= indexLookup.IsCompletedSuccessfully;
            var sample = (await indexLookup)!.Value;
            var time = new MediaTimestamp(number * 1001, 30000);
            var timeLookup = track.Samples.FindSampleAsync(time, CancellationToken.None);
            allSynchronous &= timeLookup.IsCompletedSuccessfully;
            var byTime = (await timeLookup)!.Value;
            var seekLookup = track.Samples.SeekAsync(time, CancellationToken.None);
            allSynchronous &= seekLookup.IsCompletedSuccessfully;
            var seek = (await seekLookup)!.Value;
            checksum += sample.Index + byTime.Index + seek.SyncSample.Index;
        }
        var lookupBytes = GC.GetAllocatedBytesForCurrentThread() - beforeLookup;

        // Assert: fixed budgets, identical for 64 and 1,000,000 samples; also prohibit hidden enumeration/work expansion.
        constructionBytes.ShouldBeLessThanOrEqualTo(1024);
        lookupBytes.ShouldBeLessThanOrEqualTo(1024);
        allSynchronous.ShouldBeTrue();
        Environment.CurrentManagedThreadId.ShouldBe(measuringThread);
        index.EnumerationsStarted.ShouldBe(0);
        index.SamplesProduced.ShouldBe(iterations * 4L);
        checksum.ShouldBeGreaterThan(0);
        _output.WriteLine($"Samples={count}; construction={constructionBytes} bytes; {iterations} index/time/seek sets={lookupBytes} bytes; descriptors={index.SamplesProduced}.");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Random lookup retains 64-bit offsets beyond 40 GB")]
    public async Task GetSampleAsync_LastSample_ReturnsExactSourceCoordinates()
    {
        // Arrange
        IMediaSampleIndex index = new GeneratedMediaSampleIndex(1000000);

        // Act
        var sample = (await index.GetSampleAsync(999999, CancellationToken.None))!.Value;
        var missing = await index.GetSampleAsync(1000000, CancellationToken.None);

        // Assert
        sample.Index.ShouldBe(999999);
        sample.TrackId.ShouldBe(1UL);
        sample.ByteOffset.ShouldBe(65535938560L);
        sample.ByteOffset.ShouldBeGreaterThan(40L * 1024 * 1024 * 1024);
        sample.DecodeTime.ShouldBe(new MediaTimestamp(999999L * 3003, 90000));
        missing.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Streaming stops and releases its enumerator without expanding the track")]
    public async Task ReadSamplesAsync_EarlyExit_ProducesOnlyRequestedSamples()
    {
        // Arrange
        var index = new GeneratedMediaSampleIndex(1000000);
        var read = 0;

        // Act
        await foreach (var sample in index.ReadSamplesAsync(CancellationToken.None))
        {
            sample.Index.ShouldBe(read);
            if (++read == 3)
            {
                break;
            }
        }

        // Assert
        index.SamplesProduced.ShouldBe(3);
        index.EnumerationsStarted.ShouldBe(1);
        index.EnumerationsDisposed.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Media] - Presentation lookup handles B-frame reordering and signed edit-like shifts")]
    [InlineData(500)]
    [InlineData(-500)]
    public async Task FindSampleAsync_ReorderedPresentation_UsesPresentationRatherThanDecodeOrder(long shiftMilliseconds)
    {
        // Arrange: decode order 0,1,2,3 presents at ordinal 0,3,1,2.
        var shift = new MediaTimestamp(shiftMilliseconds, 1000);
        IMediaSampleIndex index = new GeneratedMediaSampleIndex(64, shift);
        long[] expectedIndexes = [0, 2, 3, 1];

        // Act / Assert
        for (var ordinal = 0; ordinal < expectedIndexes.Length; ordinal++)
        {
            var time = new MediaTimestamp(ordinal * 1001, 30000) + shift;
            var exact = (await index.FindSampleAsync(time, CancellationToken.None))!.Value;
            var between = (await index.FindSampleAsync(time + new MediaTimestamp(1, 90000), CancellationToken.None))!.Value;
            exact.Index.ShouldBe(expectedIndexes[ordinal]);
            between.Index.ShouldBe(exact.Index);
            exact.PresentationTime.ShouldBe(time);
            (exact.DecodeTime!.Value + exact.PresentationOffset!.Value).ShouldBe(time);
        }
        var beforeStart = await index.FindSampleAsync(shift - new MediaTimestamp(1, 90000), CancellationToken.None);
        var afterEnd = (await index.FindSampleAsync(new MediaTimestamp(100, 1), CancellationToken.None))!.Value;
        beforeStart.ShouldBeNull();
        afterEnd.Index.ShouldBe(61); // The final P-frame is decoded before the final two B-frames.
    }

    [Theory(DisplayName = "Cohesion Test [Content.Media] - Seek ranges follow irregular sync points and include decode dependencies")]
    [InlineData(0, 0, 0)]
    [InlineData(7, 0, 5)]
    [InlineData(8, 8, 8)]
    [InlineData(19, 8, 17)]
    [InlineData(20, 20, 20)]
    [InlineData(43, 20, 41)]
    [InlineData(44, 44, 44)]
    [InlineData(63, 44, 61)]
    [InlineData(64, 64, 64)]
    [InlineData(65, 64, 66)]
    [InlineData(66, 64, 67)]
    public async Task SeekAsync_IrregularSyncAndCompositionOffsets_ReturnsSafeContiguousRange(long ordinal, long syncIndex, long targetIndex)
    {
        // Arrange: shift emulates an initial empty edit; interleaved gaps and framing are synthetic.
        var shift = new MediaTimestamp(5, 1);
        IMediaSampleIndex index = new GeneratedMediaSampleIndex(128, shift);
        var requested = new MediaTimestamp(ordinal * 1001, 30000) + shift;

        // Act
        var result = (await index.SeekAsync(requested, CancellationToken.None))!.Value;

        // Assert
        result.SyncSample.Index.ShouldBe(syncIndex);
        result.SyncSample.IsSync.ShouldBeTrue();
        result.TargetSample.Index.ShouldBe(targetIndex);
        result.TargetSample.PresentationTime.ShouldBe(requested);
        result.ByteOffset.ShouldBe(4096 + syncIndex * 65536 - 32);
        var expectedEnd = 4096 + targetIndex * 65536 + 700 + targetIndex % 13 * 17;
        (result.ByteOffset + result.ByteLength).ShouldBe(expectedEnd);
        for (var i = syncIndex; i <= targetIndex; i++)
        {
            var dependency = (await index.GetSampleAsync(i, CancellationToken.None))!.Value;
            dependency.ByteOffset.ShouldBeGreaterThanOrEqualTo(result.ByteOffset);
            (dependency.ByteOffset + dependency.ByteLength).ShouldBeLessThanOrEqualTo(expectedEnd);
        }
        if (ordinal == 65)
        {
            var futureReference = (await index.GetSampleAsync(65, CancellationToken.None))!.Value;
            (futureReference.PresentationTime > requested).ShouldBeTrue();
            futureReference.ByteOffset.ShouldBeGreaterThan(result.ByteOffset);
            futureReference.ByteOffset.ShouldBeLessThan(result.ByteOffset + result.ByteLength);
            result.TargetSample.PresentationOffset.ShouldBe(shift - new MediaTimestamp(1001, 30000));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Missing targets and sync points produce no seek result")]
    public async Task SeekAsync_UnavailableSamples_ReturnsNull()
    {
        // Arrange
        IMediaSampleIndex empty = new GeneratedMediaSampleIndex(0);
        IMediaSampleIndex noSync = new GeneratedMediaSampleIndex(64, hasSync: false);
        IMediaSampleIndex shifted = new GeneratedMediaSampleIndex(64, new MediaTimestamp(5, 1));

        // Act / Assert
        (await empty.GetSampleAsync(0, CancellationToken.None)).ShouldBeNull();
        (await empty.FindSampleAsync(default, CancellationToken.None)).ShouldBeNull();
        (await empty.SeekAsync(default, CancellationToken.None)).ShouldBeNull();
        (await noSync.SeekAsync(new MediaTimestamp(1, 1), CancellationToken.None)).ShouldBeNull();
        (await shifted.SeekAsync(new MediaTimestamp(4999, 1000), CancellationToken.None)).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Streaming-only indexes do not silently scan for random lookup")]
    public async Task GetSampleAsync_StreamingOnly_RejectsUnsupportedCapabilities()
    {
        // Arrange
        IMediaSampleIndex index = new StreamingMediaSampleIndex();
        var count = 0;

        // Act
        await foreach (var sample in index.ReadSamplesAsync(CancellationToken.None))
        {
            count++;
        }

        // Assert
        count.ShouldBe(0);
        index.Count.ShouldBeNull();
        index.CanLookupByIndex.ShouldBeFalse();
        index.CanLookupByTime.ShouldBeFalse();
        index.CanSeek.ShouldBeFalse();
        await Should.ThrowAsync<NotSupportedException>(async () => await index.GetSampleAsync(0, CancellationToken.None));
        await Should.ThrowAsync<NotSupportedException>(async () => await index.FindSampleAsync(default, CancellationToken.None));
        await Should.ThrowAsync<NotSupportedException>(async () => await index.SeekAsync(default, CancellationToken.None));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Invalid lookup indexes are rejected before accessing the source")]
    public async Task GetSampleAsync_NegativeIndex_ThrowsWithoutReading()
    {
        // Arrange
        var index = new GeneratedMediaSampleIndex(64);

        // Act / Assert
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await index.GetSampleAsync(-1, CancellationToken.None));
        index.SamplesProduced.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Cancellation prevents lookup and interrupts enumeration")]
    public async Task ReadSamplesAsync_CanceledToken_StopsWithoutExtraSamples()
    {
        // Arrange
        var index = new GeneratedMediaSampleIndex(1000000);
        using var cancellation = new CancellationTokenSource();
        await using var reader = index.ReadSamplesAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        (await reader.MoveNextAsync()).ShouldBeTrue();

        // Act
        cancellation.Cancel();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());
        await Should.ThrowAsync<OperationCanceledException>(async () => await index.GetSampleAsync(1, cancellation.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () => await index.FindSampleAsync(default, cancellation.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () => await index.SeekAsync(default, cancellation.Token));
        index.SamplesProduced.ShouldBe(1);
        index.EnumerationsDisposed.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - A pre-canceled enumeration does not open its source")]
    public async Task ReadSamplesAsync_PreCanceledToken_DoesNotStartCoreEnumeration()
    {
        // Arrange
        var index = new GeneratedMediaSampleIndex(64);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var reader = index.ReadSamplesAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());
        index.EnumerationsStarted.ShouldBe(0);
    }
}
