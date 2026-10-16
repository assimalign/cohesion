using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Media.Tests;

// Synthetic anamorphic video metadata; 720x576 encoded pixels displayed at 16:9.
internal sealed class TestVideoTrack : TestMediaTrack, IMediaVideoTrack
{
    internal TestVideoTrack(BinarySegment codecData)
        : base(MediaTrackKind.Video, new StreamingMediaSampleIndex(), codecData) { }

    public ulong? PixelWidth => 720;
    public ulong? PixelHeight => 576;
    public (ulong Width, ulong Height)? DisplayAspectRatio => (16, 9);
}
