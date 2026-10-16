using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Media.Tests;

// Container-neutral metadata made in code; no media files or codec parsing are used.
internal sealed class TestMediaTrack : IMediaTrack
{
    internal TestMediaTrack(MediaTrackKind kind, IMediaSampleIndex samples)
    {
        Kind = kind;
        Samples = samples;
    }

    public ulong Id => Samples.TrackId;
    public MediaTrackKind Kind { get; }
    public string CodecId => "synthetic";
    public BinarySegment CodecPrivateData => default;
    public string? Language => "en-US";
    public long Timescale => 90000;
    public MediaTimestamp? Duration => null;
    public bool IsEnabled => true;
    public bool? IsDefault => null;
    public IMediaSampleIndex Samples { get; }
}
