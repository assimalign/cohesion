using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Media;

namespace Assimalign.Cohesion.Content.Mpeg.Internal;

internal sealed class Mp4VideoTrack : Mp4Track, IMediaVideoTrack
{
    internal Mp4VideoTrack(TrackHeaderBox header, MediaHeaderBox media, HandlerBox handler, BmffBox entry, Mp4SampleIndex index, uint movieScale)
        : base(header, media, handler, entry, index, movieScale)
    {
        if (entry is IMp4SampleEntry known && entry.BoxType.ToFourCC().ToString() is "avc1" or "avc3" or "hvc1" or "hev1" or "av01" or "vp09")
        {
            PixelWidth = known.Fields.Width == 0 ? null : known.Fields.Width;
            PixelHeight = known.Fields.Height == 0 ? null : known.Fields.Height;
            DisplayAspectRatio = Aspect(entry, header);
        }
    }
    public ulong? PixelWidth { get; }
    public ulong? PixelHeight { get; }
    public (ulong Width, ulong Height)? DisplayAspectRatio { get; }
}
