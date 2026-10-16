namespace Assimalign.Cohesion.Content.Media.Tests;

// Synthetic 48 kHz stereo metadata independent of the track's 90 kHz clock.
internal sealed class TestAudioTrack : TestMediaTrack, IMediaAudioTrack
{
    internal TestAudioTrack() : base(MediaTrackKind.Audio, new StreamingMediaSampleIndex()) { }

    public double? SampleRate => 48000;
    public ulong? ChannelCount => 2;
}
