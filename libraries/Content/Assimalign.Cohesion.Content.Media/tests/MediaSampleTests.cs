using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Media.Tests;

public class MediaSampleTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Media] - A sample retains exact composition and timeline shifts")]
    public void Constructor_KnownDecodeTime_ComputesEffectivePresentationOffset()
    {
        // Arrange: -1 frame composition offset plus an initial five-second empty edit.
        var decode = new MediaTimestamp(6006, 90000);
        var presentation = new MediaTimestamp(1001, 30000) + new MediaTimestamp(5, 1);

        // Act
        var sample = new MediaSample(7, 2, decode, presentation, 50000000000, 1024, false);

        // Assert
        sample.TrackId.ShouldBe(7UL);
        sample.Index.ShouldBe(2);
        sample.DecodeTime.ShouldBe(decode);
        sample.PresentationTime.ShouldBe(presentation);
        sample.PresentationOffset.ShouldBe(new MediaTimestamp(148999, 30000));
        sample.ByteOffset.ShouldBe(50000000000);
        sample.ByteLength.ShouldBe(1024);
        sample.IsSync.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Matroska-like blocks need no invented decode timestamp")]
    public void Constructor_UnknownDecodeTime_PreservesPresentationOnly()
    {
        // Arrange: a block timestamp after subtracting a container CodecDelay, with no explicit DTS.
        var presentation = new MediaTimestamp(-6500000, 1000000000);

        // Act
        var sample = new MediaSample(ulong.MaxValue, 0, null, presentation, 128, 20, true);

        // Assert
        sample.TrackId.ShouldBe(ulong.MaxValue);
        sample.DecodeTime.ShouldBeNull();
        sample.PresentationOffset.ShouldBeNull();
        sample.PresentationTime.ShouldBe(presentation);
        sample.IsSync.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Media] - Invalid sample coordinates are rejected at construction")]
    [InlineData(0UL, 0, 0, 1)]
    [InlineData(1UL, -1, 0, 1)]
    [InlineData(1UL, 0, -1, 1)]
    [InlineData(1UL, 0, 0, -1)]
    [InlineData(1UL, 0, long.MaxValue, 1)]
    [InlineData(1UL, 0, 1, long.MaxValue)]
    public void Constructor_InvalidCoordinates_ThrowsArgumentOutOfRange(ulong track, long index, long offset, long length)
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => new MediaSample(track, index, null, default, offset, length, false));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Sample endpoint arithmetic permits the largest valid coordinate")]
    public void Constructor_EmptyAndMaximumRanges_PreservesCoordinates()
    {
        // Arrange / Act
        var empty = new MediaSample(1, 0, null, default, long.MaxValue, 0, false);
        var largest = new MediaSample(1, 1, null, default, 0, long.MaxValue, false);

        // Assert
        empty.ByteOffset.ShouldBe(long.MaxValue);
        empty.ByteLength.ShouldBe(0);
        largest.ByteLength.ShouldBe(long.MaxValue);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - An unrepresentable effective offset is refused")]
    public void Constructor_OffsetOverflow_ThrowsOverflow()
    {
        // Arrange
        var decode = new MediaTimestamp(long.MinValue, 1);
        var presentation = new MediaTimestamp(long.MaxValue, 1);

        // Act / Assert
        Should.Throw<OverflowException>(() => new MediaSample(1, 0, decode, presentation, 0, 1, true));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Sample equality uses exact timestamp equality")]
    public void Equals_EquivalentTimestampRepresentations_MatchesValueAndHash()
    {
        // Arrange
        var first = new MediaSample(1, 3, new MediaTimestamp(90000, 90000), new MediaTimestamp(2000, 1000), 30, 10, false);
        var second = new MediaSample(1, 3, new MediaTimestamp(48000, 48000), new MediaTimestamp(2, 1), 30, 10, false);
        var different = new MediaSample(1, 4, first.DecodeTime, first.PresentationTime, 30, 10, false);

        // Act / Assert
        first.Equals(second).ShouldBeTrue();
        first.Equals((object)second).ShouldBeTrue();
        first.GetHashCode().ShouldBe(second.GetHashCode());
        first.Equals(different).ShouldBeFalse();
        first.Equals(null).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Seek ranges accept conservative framing and interleaving")]
    public void Constructor_ValidSeekRange_CoversSamplesAndRetainsValueEquality()
    {
        // Arrange
        var sync = new MediaSample(1, 2, null, new MediaTimestamp(1, 1), 100, 20, true);
        var target = new MediaSample(1, 5, null, new MediaTimestamp(2, 1), 300, 30, false);

        // Act
        var result = new MediaSeekResult(sync, target, 80, 260);
        var copy = new MediaSeekResult(sync, target, 80, 260);

        // Assert
        result.SyncSample.ShouldBe(sync);
        result.TargetSample.ShouldBe(target);
        result.ByteOffset.ShouldBe(80);
        result.ByteLength.ShouldBe(260);
        result.Equals(copy).ShouldBeTrue();
        result.Equals((object)copy).ShouldBeTrue();
        result.GetHashCode().ShouldBe(copy.GetHashCode());
        result.Equals(new MediaSeekResult(sync, target, 80, 261)).ShouldBeFalse();
        result.Equals(null).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Media] - Incomplete or overflowing seek ranges are rejected")]
    [InlineData(101, 229)]
    [InlineData(80, 249)]
    [InlineData(-1, 331)]
    [InlineData(80, -1)]
    [InlineData(80, long.MaxValue)]
    public void Constructor_InvalidSeekRange_ThrowsArgumentException(long offset, long length)
    {
        // Arrange
        var sync = new MediaSample(1, 2, null, new MediaTimestamp(1, 1), 100, 20, true);
        var target = new MediaSample(1, 5, null, new MediaTimestamp(2, 1), 300, 30, false);

        // Act / Assert
        Should.Throw<ArgumentException>(() => new MediaSeekResult(sync, target, offset, length));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Invalid sync identity and ordering cannot become seek results")]
    public void Constructor_InvalidSeekSamples_RejectsMismatchedOrUnusableSamples()
    {
        // Arrange
        var sync = new MediaSample(1, 2, null, new MediaTimestamp(1, 1), 100, 20, true);
        var target = new MediaSample(1, 5, null, new MediaTimestamp(2, 1), 300, 30, false);
        var wrongTrack = new MediaSample(2, 5, null, new MediaTimestamp(2, 1), 300, 30, false);
        var earlierIndex = new MediaSample(1, 1, null, new MediaTimestamp(2, 1), 300, 30, false);
        var earlierTime = new MediaSample(1, 5, null, default, 300, 30, false);

        // Act / Assert
        Should.Throw<ArgumentException>(() => new MediaSeekResult(default, target, 0, 1000));
        Should.Throw<ArgumentException>(() => new MediaSeekResult(sync, default, 0, 1000));
        Should.Throw<ArgumentException>(() => new MediaSeekResult(target, target, 0, 1000));
        Should.Throw<ArgumentException>(() => new MediaSeekResult(sync, wrongTrack, 0, 1000));
        Should.Throw<ArgumentException>(() => new MediaSeekResult(sync, earlierIndex, 0, 1000));
        Should.Throw<ArgumentException>(() => new MediaSeekResult(sync, earlierTime, 0, 1000));
    }
}
