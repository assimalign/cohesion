using System;

using Assimalign.Cohesion.Content.Ebml;
using Assimalign.Cohesion.Content.Media;

namespace Assimalign.Cohesion.Content.Mkv.Internal;

internal sealed class MkvAudioTrack : MkvTrack, IMediaAudioTrack
{
    internal MkvAudioTrack(MkvDocument document, EbmlElement element) : base(document, element)
    {
        var audio = MkvElements.Find(element, 0xE1);
        if (audio is null)
        { throw new ContentFormatException(ErrorMessages.AudioRequired, ContentFormatErrorCode.InvalidStructure, element.Offset); }
        SampleRate = MkvElements.Find(audio, 0xB5)?.Value is double frequency ? frequency : 8000;
        ChannelCount = MkvElements.Number(audio, 0x9F, 1);
        if (!double.IsFinite(SampleRate.Value) || SampleRate <= 0)
        { throw new ContentFormatException(ErrorMessages.AudioInvalid, ContentFormatErrorCode.InvalidValue, audio.Offset); }
    }
    public double? SampleRate { get; }
    public ulong? ChannelCount { get; }
}
