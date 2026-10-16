using System;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

// Original, license-clean fixtures created with this package's writer. No downloaded media.
internal static class BmffModelFixtures
{
    internal static readonly (string Type, string Clause)[] Cases =
    [
        ("mvhd", "8.2.2"), ("mehd", "8.8.2"), ("trex", "8.8.3"), ("tkhd", "8.3.2"), ("elst", "8.6.6"),
        ("mdhd", "8.4.2"), ("hdlr", "8.4.3"), ("vmhd", "8.4.5.2"), ("smhd", "8.4.5.3"), ("nmhd", "8.4.5.5"), ("hmhd", "8.4.5.4"),
        ("url ", "8.7.2"), ("urn ", "8.7.2"), ("dref", "8.7.2"), ("stsd", "8.5.2"), ("stts", "8.6.1.2"), ("ctts", "8.6.1.3"),
        ("stss", "8.6.2"), ("stsz", "8.7.3.2"), ("stz2", "8.7.3.3"), ("stsc", "8.7.4"), ("stco", "8.7.5"), ("co64", "8.7.5"),
        ("sdtp", "8.6.4"), ("sbgp", "8.9.2"), ("sgpd", "8.9.3"), ("subs", "8.7.7"), ("mfhd", "8.8.5"), ("tfhd", "8.8.7"),
        ("tfdt", "8.8.12"), ("trun", "8.8.8"), ("tfra", "8.8.10"), ("mfro", "8.8.11"), ("meta", "8.11.1"), ("cprt", "8.10.2"),
        ("xml ", "8.11.2"), ("bxml", "8.11.2"), ("pitm", "8.11.4"), ("iloc", "8.11.3"), ("iinf", "8.11.6"), ("infe", "8.11.6"),
        ("pdin", "8.1.3"), ("frma", "8.12.2"), ("schm", "8.12.4"), ("tref", "8.3.3")
    ];

    internal static BmffBox Create(string type) => type switch
    {
        "mvhd" => new MovieHeaderBox(0, 0) { Version = 1, CreationTime = ulong.MaxValue, Duration = 42, NextTrackId = 3 },
        "mehd" => new MovieExtensionHeaderBox(0, 0) { Version = 1, FragmentDuration = 100 },
        "trex" => new TrackExtensionBox(0, 0) { TrackId = 1, DefaultSampleDescriptionIndex = 1, DefaultSampleDuration = 3000 },
        "tkhd" => new TrackHeaderBox(0, 0) { Version = 1, Flags = 3, TrackId = 1, Duration = 42, Width = UnsignedFixedPoint16x16.FromRaw(1920u << 16), Height = UnsignedFixedPoint16x16.FromRaw(1080u << 16) },
        "elst" => new EditListBox(0, 0) { Version = 1, Entries = [new() { SegmentDuration = 42, MediaTime = -1, MediaRateInteger = 1 }] },
        "mdhd" => new MediaHeaderBox(0, 0) { Version = 1, TimeScale = 48000, Duration = 1024, PackedLanguage = 0x15c7 },
        "hdlr" => new HandlerBox(0, 0) { HandlerType = FourCC.Parse("vide"), Name = "Video" },
        "vmhd" => new VideoMediaHeaderBox(0, 0) { Flags = 1, OpColorRed = 12 },
        "smhd" => new SoundMediaHeaderBox(0, 0) { Balance = FixedPoint8x8.FromRaw(-32) },
        "nmhd" => new NullMediaHeaderBox(0, 0),
        "hmhd" => new HintMediaHeaderBox(0, 0) { MaxPduSize = 1400, AveragePduSize = 900, MaxBitrate = 12000 },
        "url " => new DataEntryUrlBox { Location = "media.bin" },
        "urn " => new DataEntryUrnBox { Name = "example", Location = "media.bin" },
        "dref" => Container(new DataReferenceBox(0, 0), new DataEntryUrlBox { Flags = 1 }, Create("urn ")),
        "stsd" => Container(new SampleDescriptionBox(0, 0), Opaque("avc1", new byte[78]), Opaque("mp4a", new byte[28])),
        "stts" => new SampleDecodingTimeBox(0, 0) { Entries = [new() { SampleCount = 2, SampleDelta = 3000 }] },
        "ctts" => new SampleCompositionTimeBox(0, 0) { Version = 1, Entries = [new() { SampleCount = 2, SampleOffset = -3 }] },
        "stss" => new SyncSampleTableBox(0, 0) { Entries = [1, 3] },
        "stsz" => new SampleSizeBox(0, 0) { Entries = [3, 4] },
        "stz2" => new CompactSampleSizeBox(0, 0) { FieldSize = 4, Entries = [1, 2, 15] },
        "stsc" => new SampleToChunkBox(0, 0) { Entries = [new() { FirstChunk = 1, SamplesPerChunk = 2, SampleDescriptionIndex = 1 }] },
        "stco" => new ChunkOffsetBox(0, 0) { Entries = [100, uint.MaxValue] },
        "co64" => new ChunkOffset64BitBox(0, 0) { Entries = [100, (ulong)uint.MaxValue + 1] },
        "sdtp" => new DisposableSampleBox(0, 0) { Entries = [new() { IsLeading = 1, DependsOn = 2, IsDependedOn = 1, HasRedundancy = 2 }] },
        "sbgp" => new SampleToGroupBox(0, 0) { Version = 1, GroupingType = FourCC.Parse("roll"), GroupingTypeParameter = 42, Entries = [new() { SampleCount = 2, GroupDescriptionIndex = 1 }] },
        "sgpd" => new SampleGroupDescriptionBox(0, 0) { Version = 2, GroupingType = FourCC.Parse("roll"), DefaultLength = 0, DefaultSampleDescriptionIndex = 1, Entries = [new byte[] { 0xff, 0xff }] },
        "subs" => new SampleSubInfoBox(0, 0) { Version = 1, Entries = [new() { SampleDelta = 1, Subsamples = [new() { Size = 70000, Priority = 2, Discardable = 1, CodecSpecificParameters = 17 }] }] },
        "mfhd" => new MovieFragmentHeaderBox(0, 0) { SequenceNumber = 7 },
        "tfhd" => new TrackFragmentHeaderBox(0, 0) { Flags = 0x3b, TrackId = 1, BaseDataOffset = 100, SampleDescriptionIndex = 1, DefaultSampleDuration = 42, DefaultSampleSize = 3, DefaultSampleFlags = 0x2000000 },
        "tfdt" => new TrackFragmentDecodeTimeBox { Version = 1, BaseMediaDecodeTime = 17 },
        "trun" => new TrackFragmentRunBox(0, 0) { Version = 1, Flags = 0xf01, DataOffset = -20, Entries = [new() { Duration = 42, Size = 3, Flags = 0x2000000, CompositionTimeOffset = -4 }] },
        "tfra" => new TrackFragmentRandomAccessBox(0, 0) { Version = 1, TrafNumberSize = 2, TrunNumberSize = 3, SampleNumberSize = 4, Entries = [new() { Time = 7, MoofOffset = 1234, TrafNumber = 1, TrunNumber = 1, SampleNumber = 1 }] },
        "mfro" => new MovieFragmentRandomAccessOffsetBox(0, 0) { MfraSize = 200 },
        "meta" => Container(new MetaBox(0, 0), Create("hdlr"), Create("pitm")),
        "cprt" => new CopyrightBox(0, 0) { PackedLanguage = 0x15c7, Notice = "Original test content" },
        "xml " => new XmlBox(0, 0) { Xml = "<test/>" },
        "bxml" => new BinaryXmlBox(0, 0) { Data = [0x80, 0, 0xff] },
        "pitm" => new PrimaryItemReferenceBox(0, 0) { Version = 1, ItemId = 70000 },
        "iloc" => new ItemLocationBox(0, 0) { Version = 2, OffsetSize = 8, LengthSize = 4, BaseOffsetSize = 8, IndexSize = 4, Entries = [new() { ItemId = 70000, BaseOffset = 100, Extents = [new() { Index = 1, Offset = 12, Length = 3 }] }] },
        "iinf" => Container(new ItemInfoBox(0, 0) { Version = 1 }, Create("infe")),
        "infe" => new ItemInfoEntryBox { Version = 3, ItemId = 70000, ItemType = FourCC.Parse("mime"), ItemName = "Test", ContentType = "text/plain", HasContentEncoding = true, ContentEncoding = "identity" },
        "pdin" => new ProgressiveDownloadInfoBox(0, 0) { Entries = [new() { Rate = 1000, InitialDelay = 50 }] },
        "frma" => new OriginalFormatBox(0, 0) { DataFormat = FourCC.Parse("avc1") },
        "schm" => new SchemeTypeBox(0, 0) { Flags = 1, SchemeType = FourCC.Parse("cenc"), SchemeVersion = 0x10000, SchemeUri = "urn:example" },
        "tref" => Container(new TrackReferenceBox(0, 0), Reference()),
        _ => throw new ArgumentException(nameof(type))
    };

    private static TrackReferenceTypeBox Reference()
    {
        var box = TrackReferenceTypeBox.Create(FourCC.Parse("cdsc"));
        box.TrackIds = [1, 2];
        return box;
    }

    internal static T Container<T>(T parent, params BmffBox[] children) where T : BmffBoxComposite
    {
        foreach (var child in children)
        {
            parent.Children.Add(child);
        }
        return parent;
    }

    internal static UnknownBox Opaque(string type, byte[] bytes) =>
        UnknownBox.Create(BmffBoxHeader.Create(FourCC.Parse(type).ToBmffBoxType(), bytes.Length), BinarySegment.Create(bytes));

    internal static byte[] Write(BmffBox box)
    {
        using var memory = new MemoryStream();
        using var writer = BmffWriter.Create(memory);
        writer.Write(box);
        return memory.ToArray();
    }
}
