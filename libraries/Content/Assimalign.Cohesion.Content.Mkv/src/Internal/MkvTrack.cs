using System;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml;
using Assimalign.Cohesion.Content.Media;

namespace Assimalign.Cohesion.Content.Mkv.Internal;

internal class MkvTrack : IMediaTrack
{
    internal MkvTrack(MkvDocument document, EbmlElement element)
    {
        Element = element;
        Id = MkvElements.Number(element, 0xD7);
        Kind = MkvElements.Number(element, 0x83) switch
        { 1 => MediaTrackKind.Video, 2 => MediaTrackKind.Audio, 17 => MediaTrackKind.Text, 33 => MediaTrackKind.Metadata, _ => MediaTrackKind.Other };
        Samples = new MkvSampleIndex(document, this);
    }
    internal static MkvTrack Create(MkvDocument document, EbmlElement element) => MkvElements.Number(element, 0x83) switch
    {
        1 => new MkvVideoTrack(document, element),
        2 => new MkvAudioTrack(document, element),
        _ => new MkvTrack(document, element)
    };
    internal EbmlElement Element { get; }
    internal ulong CodecDelay => MkvElements.Number(Element, 0x56AA);
    internal ulong SeekPreRoll => MkvElements.Number(Element, 0x56BB);
    internal ulong DefaultDuration => MkvElements.Number(Element, 0x23E383);
    internal bool AllowsLacing => MkvElements.Number(Element, 0x9C, 1) != 0;
    internal bool SupportsSeek => MkvElements.Find(Element, 0x6D80) is null && MkvElements.Find(Element, 0xE2) is null;
    public ulong Id { get; }
    public MediaTrackKind Kind { get; }
    public string CodecId => MkvElements.Text(Element, 0x86)!;
    public BinarySegment CodecPrivateData => MkvElements.Find(Element, 0x63A2)?.Value is BinarySegment data ? data : default;
    public string? Language => MkvElements.Text(Element, 0x22B59D) ?? MkvElements.Text(Element, 0x22B59C) ?? "eng";
    public long Timescale => 1_000_000_000;
    public MediaTimestamp? Duration => null; // No unrequested whole-track scan or guess from file Duration.
    public bool IsEnabled => MkvElements.Number(Element, 0xB9, 1) != 0;
    public bool? IsDefault => MkvElements.Number(Element, 0x88, 1) != 0;
    public IMediaSampleIndex Samples { get; }
}
