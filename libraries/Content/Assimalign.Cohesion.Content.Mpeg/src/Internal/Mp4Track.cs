using System;
using System.Buffers.Binary;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Media;

namespace Assimalign.Cohesion.Content.Mpeg.Internal;

[global::System.Diagnostics.DebuggerDisplay("Mp4Track {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal class Mp4Track : IMediaTrack
{
    private readonly TrackHeaderBox _header;
    private readonly uint _movieScale;
    internal Mp4Track(TrackHeaderBox header, MediaHeaderBox media, HandlerBox handler, BmffBox entry, Mp4SampleIndex index, uint movieScale)
    {
        _header = header;
        _movieScale = movieScale;
        Id = header.TrackId;
        Timescale = media.TimeScale;
        CodecId = entry.BoxType.ToFourCC().ToString();
        var language = media.Language;
        Language = Mp4Language.ToLanguageTag(language);
        Kind = handler.HandlerType.ToString() switch
        {
            "vide" => MediaTrackKind.Video,
            "soun" => MediaTrackKind.Audio,
            "text" or "sbtl" or "subt" or "clcp" => MediaTrackKind.Text,
            "meta" or "mdta" => MediaTrackKind.Metadata,
            _ => MediaTrackKind.Other
        };
        Index = index;
        Entry = entry;
        var configurationType = CodecId switch
        {
            "avc1" or "avc3" => "avcC", "hvc1" or "hev1" => "hvcC", "av01" => "av1C", "vp09" => "vpcC",
            "mp4a" => "esds", "Opus" => "dOps", "ac-3" => "dac3", "ec-3" => "dec3", _ => null
        };
        if (entry is BmffBoxComposite composite)
        {
            var found = false;
            foreach (var box in composite.Children)
            {
                if (box.BoxType.ToFourCC().ToString() == configurationType && box is IMp4OpaqueBox opaque)
                {
                    if (found) { throw new ContentFormatException(ErrorMessages.DuplicateBox, ContentFormatErrorCode.InvalidStructure, box.Offset); }
                    CodecPrivateData = opaque.Data;
                    found = true;
                }
            }
            if (configurationType is not null && !found)
            {
                throw new ContentFormatException(ErrorMessages.ConfigurationMissing, ContentFormatErrorCode.InvalidStructure, entry.Offset);
            }
        }
    }
    public ulong Id { get; }
    public MediaTrackKind Kind { get; }
    public string CodecId { get; }
    public BinarySegment CodecPrivateData { get; }
    public string? Language { get; }
    public long Timescale { get; }
    public MediaTimestamp? Duration => Index.Edits.Duration ??
        (_header.Duration != 0 && _header.Duration != (_header.Version == 0 ? uint.MaxValue : ulong.MaxValue) ?
            Timestamp(_header.Duration, _movieScale, _header.Offset) : new MediaTimestamp(Index.PresentationEnd, Timescale));
    public bool IsEnabled => (_header.Flags & 1) != 0;
    public bool? IsDefault => null;
    public IMediaSampleIndex Samples => Index;
    internal Mp4SampleIndex Index { get; }
    internal BmffBox Entry { get; }
    internal bool IsLocal { get; set; }
    internal uint DescriptionCount { get; set; }
    internal uint DescriptionIndex { get; set; }

    internal static MediaTimestamp Timestamp(ulong value, uint scale, long position)
    {
        if (value > long.MaxValue)
        {
            throw new ContentFormatException(ErrorMessages.TimestampRange, ContentFormatErrorCode.InvalidValue, position);
        }
        return new MediaTimestamp((long)value, scale);
    }

    internal static (ulong Width, ulong Height)? Aspect(BmffBox entry, TrackHeaderBox header)
    {
        if (entry is not IMp4SampleEntry known) { return null; }
        if (entry is BmffBoxComposite composite && Mp4Tree.Contains(composite.Children, 0x636c6170)) { return null; }
        if (header.Width.RawValue != 0 && header.Height.RawValue != 0)
        {
            return (header.Width.RawValue, header.Height.RawValue);
        }
        ulong width = known.Fields.Width;
        ulong height = known.Fields.Height;
        if (entry is BmffBoxComposite visual)
        {
            var aspect = Mp4Tree.Child<PixelAspectRatioBox>(visual.Children);
            if (aspect is not null)
            {
                if (aspect.Data.Length < 8)
                {
                    throw new ContentFormatException(ErrorMessages.EntryInvalid, ContentFormatErrorCode.Truncated, aspect.Offset);
                }
                Span<byte> bytes = stackalloc byte[8];
                using var stream = aspect.Data.OpenRead();
                stream.ReadContentExactly(bytes);
                var horizontal = BinaryPrimitives.ReadUInt32BigEndian(bytes);
                var vertical = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
                if (horizontal == 0 || vertical == 0)
                {
                    throw new ContentFormatException(ErrorMessages.EntryInvalid, ContentFormatErrorCode.InvalidValue, aspect.Offset);
                }
                width *= horizontal;
                height *= vertical;
            }
        }
        return width == 0 || height == 0 ? null : (width, height);
    }

    private sealed class DebugView
    {
        private readonly Mp4Track _target;

        public DebugView(Mp4Track value)
        {
            _target = value;
        }

        public ulong Id => _target.Id;
        public MediaTrackKind Kind => _target.Kind;
        public string CodecId => _target.CodecId;
        public BinarySegment CodecPrivateData => _target.CodecPrivateData;
        public string? Language => _target.Language;
        public long Timescale => _target.Timescale;
        public bool IsEnabled => _target.IsEnabled;
        public bool? IsDefault => _target.IsDefault;
        public IMediaSampleIndex Samples => _target.Samples;
    }
}
