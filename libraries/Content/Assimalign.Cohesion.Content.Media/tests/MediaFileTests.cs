using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Media.Tests;

public class MediaFileTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Media] - Unknown duration is independent of live state")]
    public void Duration_LiveAndFiniteViews_DoesNotInferLivenessFromMissingMetadata()
    {
        // Arrange
        using var unknown = new MemoryMediaFile(MediaTrackKind.Video);
        using var live = new MemoryMediaFile(MediaTrackKind.Video, isLive: true);
        using var finite = new MemoryMediaFile(MediaTrackKind.Audio, duration: new MediaTimestamp(48000, 48000));

        // Act / Assert
        unknown.Duration.ShouldBeNull();
        unknown.IsLive.ShouldBeFalse();
        live.Duration.ShouldBeNull();
        live.IsLive.ShouldBeTrue();
        finite.Duration.ShouldBe(new MediaTimestamp(1, 1));
        finite.IsLive.ShouldBeFalse();
        finite.Tracks[0].Id.ShouldBe(finite.Tracks[0].Samples.TrackId);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Codec private bytes remain lazy while track metadata is inspected")]
    public void CodecPrivateData_SeekableSource_DoesNotReadUntilOpened()
    {
        // Arrange
        using var source = new ObservedMediaStream();
        var data = BinarySegment.Create(source, 2, 4);

        // Act
        IMediaVideoTrack track = new TestVideoTrack(data);
        var codec = track.CodecPrivateData;

        // Assert
        track.Kind.ShouldBe(MediaTrackKind.Video);
        track.PixelWidth.ShouldBe(720UL);
        track.PixelHeight.ShouldBe(576UL);
        track.DisplayAspectRatio.ShouldBe(((ulong)16, (ulong)9));
        track.Language.ShouldBe("en-US");
        track.IsDefault.ShouldBeNull();
        track.IsEnabled.ShouldBeTrue();
        codec.IsMaterialized.ShouldBeFalse();
        codec.Length.ShouldBe(4);
        source.Reads.ShouldBe(0);
        using (var bytes = codec.OpenRead())
        {
            bytes.ReadByte().ShouldBe(2);
        }
        source.Reads.ShouldBeGreaterThan(0);
        source.CanRead.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Audio sample rate and timeline clock are separate descriptors")]
    public void SampleRate_AudioTrack_PreservesContainerProperties()
    {
        // Arrange
        IMediaAudioTrack track = new TestAudioTrack();

        // Act / Assert
        track.Kind.ShouldBe(MediaTrackKind.Audio);
        track.SampleRate.ShouldBe(48000);
        track.ChannelCount.ShouldBe(2UL);
        track.Timescale.ShouldBe(90000);
        track.CodecPrivateData.IsEmpty.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Media] - Media kinds retain the binary content contract")]
    [InlineData(MediaTrackKind.Video)]
    [InlineData(MediaTrackKind.Audio)]
    [InlineData(MediaTrackKind.Text)]
    [InlineData(MediaTrackKind.Metadata)]
    [InlineData(MediaTrackKind.Other)]
    public async Task OpenReadAsync_MediaContract_ReturnsReopenableBinaryContent(MediaTrackKind kind)
    {
        // Arrange
        using var media = new MemoryMediaFile(kind);
        IBinaryFile binary = media;
        var bytes = new byte[3];
        // Act
        await using (var stream = await binary.OpenReadAsync(CancellationToken.None))
        {
            await stream.ReadContentExactlyAsync(bytes, CancellationToken.None);
        }
        using var reopened = binary.OpenRead();
        // Assert
        media.Tracks[0].Kind.ShouldBe(kind);
        binary.CanReopen.ShouldBeTrue();
        binary.Length.ShouldBe(3);
        bytes.ShouldBe(new byte[] { 1, 2, 3 });
        reopened.ReadByte().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Media files expose ordered child content")]
    public async Task GetItemsAsync_MediaContract_ExposesItsContent()
    {
        // Arrange
        using var media = new MemoryMediaFile(MediaTrackKind.Video);
        IComposableContent composite = media;
        var count = 0;
        // Act
        await foreach (var item in composite.GetItemsAsync(CancellationToken.None))
        {
            using var stream = await item.OpenReadAsync(CancellationToken.None);
            stream.ReadByte().ShouldBe(1);
            count++;
        }
        // Assert
        count.ShouldBe(1);
    }
}
