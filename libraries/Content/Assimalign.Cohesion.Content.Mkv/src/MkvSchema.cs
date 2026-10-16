using System.Collections.Generic;

using Assimalign.Cohesion.Content.Ebml;

namespace Assimalign.Cohesion.Content.Mkv;

/// <summary>Provides the retained RFC 9559 element vocabulary and DocType registrations.</summary>
/// <remarks>Compose these schemas with the EBML reader, writer, or document. Unknown elements remain
/// opaque. WebM uses the same wire vocabulary; the Matroska document enforces the retained WebM subset.</remarks>
public static class MkvSchema
{
    /// <summary>Gets the immutable Matroska schema, including RFC 8794 headers and globals.</summary>
    public static EbmlSchema Matroska { get; } = Build();
    /// <summary>Gets the WebM schema with unsupported retained elements prohibited by occurrence rules.</summary>
    public static EbmlSchema WebM { get; } = BuildWebM();
    /// <summary>Gets the ordinal matroska and webm DocType registrations.</summary>
    public static EbmlSchemaRegistry Registry { get; } = EbmlSchemaRegistry.Create(
        new Dictionary<string, EbmlSchema> { ["matroska"] = Matroska, ["webm"] = WebM });

    private static EbmlSchema BuildWebM()
    {
        var builder = new EbmlSchemaBuilder();
        foreach (var definition in Matroska.Definitions.Values)
        {
            // webmproject.org extension flags in the retained Matroska element table.
            var prohibited = definition.Identifier.EncodedValue is 0x73A4 or 0x23314F or 0x22B59D or 0xEA or
                0x1941A469 or 0x61A7 or 0x467E or 0x466E or 0x4660 or 0x465C or 0x46AE or
                0x45BC or 0x45BD or 0x45DB or 0x45DD or 0x98 or 0x4598 or 0x437D or
                0x63C9 or 0x63C4 or 0x63C6 or 0x447B;
            builder.Add(prohibited ? EbmlElementDefinition.Create(definition.Identifier, definition.Name,
                definition.Type, definition.ParentIds, maxOccurs: 0) : definition);
        }
        return builder.Build();
    }

    private static EbmlSchema Build()
    {
        var builder = new EbmlSchemaBuilder().Include(EbmlSchema.Header);
        // RFC 9559 section 5; retained vocabulary from ebml_matroska.xml.
        // Colour and ContentEncodings children remain opaque extensions.
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x18538067), "Segment", EbmlElementType.MasterElement, unknownSizeAllowed: true, minOccurs: 1, maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x114D9B74), "SeekHead", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0x18538067)], maxOccurs: 2));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x4DBB), "Seek", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0x114D9B74)], minOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x53AB), "SeekID", EbmlElementType.Binary, parentIds: [VInt.FromEncoded(0x4DBB)], minOccurs: 1, maxOccurs: 1, minimumLength: 4, maximumLength: 4));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x53AC), "SeekPosition", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0x4DBB)], minOccurs: 1, maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x1549A966), "Info", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0x18538067)], minOccurs: 1, maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x73A4), "SegmentUUID", EbmlElementType.Binary, parentIds: [VInt.FromEncoded(0x1549A966)], maxOccurs: 1, minimumLength: 16, maximumLength: 16));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x2AD7B1), "TimestampScale", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0x1549A966)], minOccurs: 1, maxOccurs: 1, minimumValue: 1UL, defaultValue: 1000000UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x4489), "Duration", EbmlElementType.Float, parentIds: [VInt.FromEncoded(0x1549A966)], maxOccurs: 1, minimumValue: 0.0, minimumInclusive: false));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x4461), "DateUTC", EbmlElementType.Date, parentIds: [VInt.FromEncoded(0x1549A966)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x7BA9), "Title", EbmlElementType.Utf8String, parentIds: [VInt.FromEncoded(0x1549A966)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x4D80), "MuxingApp", EbmlElementType.Utf8String, parentIds: [VInt.FromEncoded(0x1549A966)], minOccurs: 1, maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x5741), "WritingApp", EbmlElementType.Utf8String, parentIds: [VInt.FromEncoded(0x1549A966)], minOccurs: 1, maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x1F43B675), "Cluster", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0x18538067)], unknownSizeAllowed: true));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xE7), "Timestamp", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0x1F43B675)], minOccurs: 1, maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xA3), "SimpleBlock", EbmlElementType.Binary, parentIds: [VInt.FromEncoded(0x1F43B675)]));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xA0), "BlockGroup", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0x1F43B675)]));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xA1), "Block", EbmlElementType.Binary, parentIds: [VInt.FromEncoded(0xA0)], minOccurs: 1, maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x9B), "BlockDuration", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xA0)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xFB), "ReferenceBlock", EbmlElementType.SignedInteger, parentIds: [VInt.FromEncoded(0xA0)]));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x1654AE6B), "Tracks", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0x18538067)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xAE), "TrackEntry", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0x1654AE6B)], minOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xD7), "TrackNumber", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xAE)], minOccurs: 1, maxOccurs: 1, minimumValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x73C5), "TrackUID", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xAE)], minOccurs: 1, maxOccurs: 1, minimumValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x83), "TrackType", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xAE)], minOccurs: 1, maxOccurs: 1, minimumValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xB9), "FlagEnabled", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xAE)], minOccurs: 1, maxOccurs: 1, maximumValue: 1UL, defaultValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x88), "FlagDefault", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xAE)], minOccurs: 1, maxOccurs: 1, maximumValue: 1UL, defaultValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x55AA), "FlagForced", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xAE)], minOccurs: 1, maxOccurs: 1, maximumValue: 1UL, defaultValue: 0UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x9C), "FlagLacing", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xAE)], minOccurs: 1, maxOccurs: 1, maximumValue: 1UL, defaultValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x23E383), "DefaultDuration", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xAE)], maxOccurs: 1, minimumValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x23314F), "TrackTimestampScale", EbmlElementType.Float, parentIds: [VInt.FromEncoded(0xAE)], minOccurs: 1, maxOccurs: 1, minimumValue: 0.0, minimumInclusive: false, defaultValue: 1.0));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x536E), "Name", EbmlElementType.Utf8String, parentIds: [VInt.FromEncoded(0xAE)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x22B59C), "Language", EbmlElementType.AsciiString, parentIds: [VInt.FromEncoded(0xAE)], minOccurs: 1, maxOccurs: 1, defaultValue: "eng"));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x22B59D), "LanguageBCP47", EbmlElementType.AsciiString, parentIds: [VInt.FromEncoded(0xAE)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x86), "CodecID", EbmlElementType.AsciiString, parentIds: [VInt.FromEncoded(0xAE)], minOccurs: 1, maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x63A2), "CodecPrivate", EbmlElementType.Binary, parentIds: [VInt.FromEncoded(0xAE)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x56AA), "CodecDelay", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xAE)], minOccurs: 1, maxOccurs: 1, defaultValue: 0UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x56BB), "SeekPreRoll", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xAE)], minOccurs: 1, maxOccurs: 1, defaultValue: 0UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xE0), "Video", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0xAE)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xB0), "PixelWidth", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xE0)], minOccurs: 1, maxOccurs: 1, minimumValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xBA), "PixelHeight", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xE0)], minOccurs: 1, maxOccurs: 1, minimumValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x54AA), "PixelCropBottom", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xE0)], minOccurs: 1, maxOccurs: 1, defaultValue: 0UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x54BB), "PixelCropTop", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xE0)], minOccurs: 1, maxOccurs: 1, defaultValue: 0UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x54CC), "PixelCropLeft", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xE0)], minOccurs: 1, maxOccurs: 1, defaultValue: 0UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x54DD), "PixelCropRight", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xE0)], minOccurs: 1, maxOccurs: 1, defaultValue: 0UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x54B0), "DisplayWidth", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xE0)], maxOccurs: 1, minimumValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x54BA), "DisplayHeight", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xE0)], maxOccurs: 1, minimumValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x54B2), "DisplayUnit", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xE0)], minOccurs: 1, maxOccurs: 1, defaultValue: 0UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x55B0), "Colour", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0xE0)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xE1), "Audio", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0xAE)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xB5), "SamplingFrequency", EbmlElementType.Float, parentIds: [VInt.FromEncoded(0xE1)], minOccurs: 1, maxOccurs: 1, minimumValue: 0.0, minimumInclusive: false, defaultValue: 8000.0));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x78B5), "OutputSamplingFrequency", EbmlElementType.Float, parentIds: [VInt.FromEncoded(0xE1)], maxOccurs: 1, minimumValue: 0.0, minimumInclusive: false));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x9F), "Channels", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xE1)], minOccurs: 1, maxOccurs: 1, minimumValue: 1UL, defaultValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x6264), "BitDepth", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xE1)], maxOccurs: 1, minimumValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x6D80), "ContentEncodings", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0xAE)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x1C53BB6B), "Cues", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0x18538067)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xBB), "CuePoint", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0x1C53BB6B)], minOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xB3), "CueTime", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xBB)], minOccurs: 1, maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xB7), "CueTrackPositions", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0xBB)], minOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xF7), "CueTrack", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xB7)], minOccurs: 1, maxOccurs: 1, minimumValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xF1), "CueClusterPosition", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xB7)], minOccurs: 1, maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xF0), "CueRelativePosition", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xB7)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xB2), "CueDuration", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xB7)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x5378), "CueBlockNumber", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xB7)], maxOccurs: 1, minimumValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xEA), "CueCodecState", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xB7)], minOccurs: 1, maxOccurs: 1, defaultValue: 0UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x1941A469), "Attachments", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0x18538067)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x61A7), "AttachedFile", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0x1941A469)], minOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x467E), "FileDescription", EbmlElementType.Utf8String, parentIds: [VInt.FromEncoded(0x61A7)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x466E), "FileName", EbmlElementType.Utf8String, parentIds: [VInt.FromEncoded(0x61A7)], minOccurs: 1, maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x4660), "FileMediaType", EbmlElementType.AsciiString, parentIds: [VInt.FromEncoded(0x61A7)], minOccurs: 1, maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x465C), "FileData", EbmlElementType.Binary, parentIds: [VInt.FromEncoded(0x61A7)], minOccurs: 1, maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x46AE), "FileUID", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0x61A7)], minOccurs: 1, maxOccurs: 1, minimumValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x1043A770), "Chapters", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0x18538067)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x45B9), "EditionEntry", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0x1043A770)], minOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x45BC), "EditionUID", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0x45B9)], maxOccurs: 1, minimumValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x45BD), "EditionFlagHidden", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0x45B9)], minOccurs: 1, maxOccurs: 1, maximumValue: 1UL, defaultValue: 0UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x45DB), "EditionFlagDefault", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0x45B9)], minOccurs: 1, maxOccurs: 1, maximumValue: 1UL, defaultValue: 0UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x45DD), "EditionFlagOrdered", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0x45B9)], minOccurs: 1, maxOccurs: 1, maximumValue: 1UL, defaultValue: 0UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0xB6), "ChapterAtom", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0x45B9)], recursive: true, minOccurs: 0));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x73C4), "ChapterUID", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xB6)], minOccurs: 1, maxOccurs: 1, minimumValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x5654), "ChapterStringUID", EbmlElementType.Utf8String, parentIds: [VInt.FromEncoded(0xB6)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x91), "ChapterTimeStart", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xB6)], minOccurs: 1, maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x92), "ChapterTimeEnd", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xB6)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x98), "ChapterFlagHidden", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xB6)], minOccurs: 1, maxOccurs: 1, maximumValue: 1UL, defaultValue: 0UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x4598), "ChapterFlagEnabled", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0xB6)], minOccurs: 1, maxOccurs: 1, maximumValue: 1UL, defaultValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x80), "ChapterDisplay", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0xB6)]));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x85), "ChapString", EbmlElementType.Utf8String, parentIds: [VInt.FromEncoded(0x80)], minOccurs: 1, maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x437C), "ChapLanguage", EbmlElementType.AsciiString, parentIds: [VInt.FromEncoded(0x80)], minOccurs: 1, defaultValue: "eng"));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x437D), "ChapLanguageBCP47", EbmlElementType.AsciiString, parentIds: [VInt.FromEncoded(0x80)]));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x437E), "ChapCountry", EbmlElementType.AsciiString, parentIds: [VInt.FromEncoded(0x80)]));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x1254C367), "Tags", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0x18538067)]));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x7373), "Tag", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0x1254C367)], minOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x63C0), "Targets", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0x7373)], minOccurs: 1, maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x68CA), "TargetTypeValue", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0x63C0)], minOccurs: 1, maxOccurs: 1, minimumValue: 1UL, defaultValue: 50UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x63CA), "TargetType", EbmlElementType.AsciiString, parentIds: [VInt.FromEncoded(0x63C0)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x63C5), "TagTrackUID", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0x63C0)], defaultValue: 0UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x63C9), "TagEditionUID", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0x63C0)], defaultValue: 0UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x63C4), "TagChapterUID", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0x63C0)], defaultValue: 0UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x63C6), "TagAttachmentUID", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0x63C0)], defaultValue: 0UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x67C8), "SimpleTag", EbmlElementType.MasterElement, parentIds: [VInt.FromEncoded(0x7373)], recursive: true, minOccurs: 0));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x45A3), "TagName", EbmlElementType.Utf8String, parentIds: [VInt.FromEncoded(0x67C8)], minOccurs: 1, maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x447A), "TagLanguage", EbmlElementType.AsciiString, parentIds: [VInt.FromEncoded(0x67C8)], minOccurs: 1, maxOccurs: 1, defaultValue: "und"));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x447B), "TagLanguageBCP47", EbmlElementType.AsciiString, parentIds: [VInt.FromEncoded(0x67C8)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x4484), "TagDefault", EbmlElementType.UnsignedInteger, parentIds: [VInt.FromEncoded(0x67C8)], minOccurs: 1, maxOccurs: 1, maximumValue: 1UL, defaultValue: 1UL));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x4487), "TagString", EbmlElementType.Utf8String, parentIds: [VInt.FromEncoded(0x67C8)], maxOccurs: 1));
        builder.Add(EbmlElementDefinition.Create(VInt.FromEncoded(0x4485), "TagBinary", EbmlElementType.Binary, parentIds: [VInt.FromEncoded(0x67C8)], maxOccurs: 1));
        return builder.Build();
    }
}
