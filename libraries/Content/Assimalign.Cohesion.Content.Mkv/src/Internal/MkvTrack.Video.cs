using Assimalign.Cohesion.Content.Ebml;
using Assimalign.Cohesion.Content.Media;

namespace Assimalign.Cohesion.Content.Mkv.Internal;

internal sealed class MkvVideoTrack : MkvTrack, IMediaVideoTrack
{
    internal MkvVideoTrack(MkvDocument document, EbmlElement element) : base(document, element)
    {
        var video = MkvElements.Find(element, 0xE0);
        if (video is null)
        { throw new ContentFormatException(ErrorMessages.VideoRequired, ContentFormatErrorCode.InvalidStructure, element.Offset); }
        var width = MkvElements.Number(video, 0xB0);
        var height = MkvElements.Number(video, 0xBA);
        PixelWidth = width; PixelHeight = height;
        var horizontal = (System.UInt128)MkvElements.Number(video, 0x54CC) + MkvElements.Number(video, 0x54DD);
        var vertical = (System.UInt128)MkvElements.Number(video, 0x54AA) + MkvElements.Number(video, 0x54BB);
        if (horizontal >= width || vertical >= height)
        { throw new ContentFormatException(ErrorMessages.VideoDimensionsInvalid, ContentFormatErrorCode.InvalidValue, video.Offset); }
        var unit = MkvElements.Number(video, 0x54B2);
        if (unit <= 3)
        {
            var displayWidth = MkvElements.Find(video, 0x54B0)?.Value as ulong?;
            var displayHeight = MkvElements.Find(video, 0x54BA)?.Value as ulong?;
            if (unit == 0) { displayWidth ??= width - (ulong)horizontal; displayHeight ??= height - (ulong)vertical; }
            if (displayWidth is { } dw && displayHeight is { } dh) { DisplayAspectRatio = (dw, dh); }
        }
    }
    public ulong? PixelWidth { get; }
    public ulong? PixelHeight { get; }
    public (ulong Width, ulong Height)? DisplayAspectRatio { get; }
}
