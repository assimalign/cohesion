using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Media;

namespace Assimalign.Cohesion.Content.Mpeg.Internal;

internal sealed class Mp4AudioTrack : Mp4Track, IMediaAudioTrack
{
    internal Mp4AudioTrack(TrackHeaderBox header, MediaHeaderBox media, HandlerBox handler, BmffBox entry, Mp4SampleIndex index, uint movieScale)
        : base(header, media, handler, entry, index, movieScale)
    {
        if (entry is IMp4SampleEntry known && entry.BoxType.ToFourCC().ToString() is "mp4a" or "Opus" or "ac-3" or "ec-3")
        {
            SampleRate = known.Fields.SampleRate == 0 ? null : known.Fields.SampleRate;
            ChannelCount = known.Fields.ChannelCount == 0 ? null : known.Fields.ChannelCount;
        }
    }
    public double? SampleRate { get; }
    public ulong? ChannelCount { get; }
}
