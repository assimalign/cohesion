using System;
using System.IO;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

// Provenance: these original MP4/HEIF-shaped byte streams are generated entirely by this
// package's writer. Sample payloads are small numbered byte patterns, not encoded media.
// The fixtures exercise ISO/IEC 14496-12 structure without claiming codec conformance.
internal static class BmffGoldenFixtures
{
    internal static byte[] Progressive(bool fastStart)
    {
        var brand = Brand("isom");
        var mediaOffset = (uint)BmffModelFixtures.Write(brand).Length + 8;
        var movie = BmffModelFixtures.Container(new MovieBox(0, 0),
            new MovieHeaderBox(0, 0) { TimeScale = 1000, Duration = 67, NextTrackId = 3 },
            Track(1, true, mediaOffset), Track(2, false, mediaOffset + 6),
            BmffModelFixtures.Container(new UserDataBox(0, 0), new CopyrightBox(0, 0) { Notice = "Original Cohesion fixture" }));
        AddUnknowns(movie);
        using var document = new BmffDocument();
        document.Boxes.Add(brand);
        document.Boxes.Add(new MediaDataBox(0, 0) { Payload = BinarySegment.Create(new byte[] { 11, 12, 13, 14, 15, 16, 21, 22, 23, 24, 25, 26 }) });
        document.Boxes.Add(movie);
        document.Boxes.Add(BmffModelFixtures.Opaque("zzzz", [0x5a]));
        using var source = new MemoryStream();
        document.Save(source);
        if (!fastStart)
        {
            return source.ToArray();
        }

        source.Position = 0;
        using var loaded = BmffDocument.Load(source);
        var moov = loaded.Boxes[2];
        loaded.Boxes.RemoveAt(2);
        loaded.Boxes.Insert(1, moov);
        using var output = new MemoryStream();
        loaded.Save(output);
        return output.ToArray();
    }

    private static TrackBox Track(uint id, bool video, uint firstOffset, bool fragmented = false)
    {
        var sampleEntry = new byte[video ? 78 : 28];
        sampleEntry[7] = 1; // SampleEntry.data_reference_index = 1 (ISO/IEC 14496-12 8.5.2).
        var table = BmffModelFixtures.Container(new SampleTableBox(0, 0),
            BmffModelFixtures.Container(new SampleDescriptionBox(0, 0), BmffModelFixtures.Opaque(video ? "avc1" : "mp4a", sampleEntry)),
            new SampleDecodingTimeBox(0, 0) { Entries = fragmented ? [] : [new() { SampleCount = 2, SampleDelta = video ? 3000u : 1024u }] },
            new SampleToChunkBox(0, 0) { Entries = fragmented ? [] : [new() { FirstChunk = 1, SamplesPerChunk = 1, SampleDescriptionIndex = 1 }] },
            new SampleSizeBox(0, 0) { Entries = fragmented ? [] : [3, 3] },
            new ChunkOffsetBox(0, 0) { Entries = fragmented ? [] : [firstOffset, firstOffset + 3] });
        if (!fragmented)
        {
            table.Children.Add(new SampleCompositionTimeBox(0, 0) { Version = 1, Entries = [new() { SampleCount = 2, SampleOffset = 0 }] });
            table.Children.Add(new SyncSampleTableBox(0, 0) { Entries = [1, 2] });
            table.Children.Add(new DisposableSampleBox(0, 0) { Entries = [new() { DependsOn = 2 }, new() { DependsOn = 2 }] });
            table.Children.Add(new SampleToGroupBox(0, 0) { GroupingType = FourCC.Parse("roll"), Entries = [new() { SampleCount = 2, GroupDescriptionIndex = 1 }] });
            table.Children.Add(new SampleGroupDescriptionBox(0, 0) { Version = 1, GroupingType = FourCC.Parse("roll"), DefaultLength = 2, Entries = [new byte[] { 0, 0 }] });
            table.Children.Add(new SampleSubInfoBox(0, 0) { Entries = [new() { SampleDelta = 1, Subsamples = [new() { Size = 3 }] }] });
        }
        var info = BmffModelFixtures.Container(new MediaInfoBox(0, 0),
            video ? new VideoMediaHeaderBox(0, 0) { Flags = 1 } : new SoundMediaHeaderBox(0, 0),
            BmffModelFixtures.Container(new DataInfoBox(0, 0), BmffModelFixtures.Container(new DataReferenceBox(0, 0), new DataEntryUrlBox { Flags = 1 })),
            table);
        return BmffModelFixtures.Container(new TrackBox(0, 0),
            new TrackHeaderBox(0, 0) { Flags = 3, TrackId = id, Duration = fragmented ? 0u : 67u, Volume = FixedPoint8x8.FromRaw(video ? (short)0 : (short)0x100), Width = UnsignedFixedPoint16x16.FromRaw(video ? 16u << 16 : 0), Height = UnsignedFixedPoint16x16.FromRaw(video ? 16u << 16 : 0) },
            BmffModelFixtures.Container(new EditBox(0, 0), new EditListBox(0, 0) { Entries = fragmented ? [] : [new() { SegmentDuration = 67, MediaTime = 0, MediaRateInteger = 1 }] }),
            BmffModelFixtures.Container(new MediaBox(0, 0),
                new MediaHeaderBox(0, 0) { TimeScale = video ? 90000u : 48000u, Duration = fragmented ? 0u : video ? 6000u : 2048u },
                new HandlerBox(0, 0) { HandlerType = FourCC.Parse(video ? "vide" : "soun"), Name = video ? "Synthetic video" : "Synthetic audio" }, info));
    }

    internal static byte[] Fragmented()
    {
        var brand = Brand("iso6");
        var movie = BmffModelFixtures.Container(new MovieBox(0, 0),
            new MovieHeaderBox(0, 0) { NextTrackId = 2 }, Track(1, true, 0, true),
            BmffModelFixtures.Container(new MovieExtensionBox(0, 0),
                new MovieExtensionHeaderBox(0, 0) { Version = 1, FragmentDuration = 12000 },
                new TrackExtensionBox(0, 0) { TrackId = 1, DefaultSampleDescriptionIndex = 1, DefaultSampleDuration = 3000, DefaultSampleSize = 3 }));
        var first = Fragment(1, 0);
        var second = Fragment(2, 6000);
        var firstOffset = (ulong)(BmffModelFixtures.Write(brand).Length + BmffModelFixtures.Write(movie).Length);
        var secondOffset = firstOffset + (ulong)BmffModelFixtures.Write(first).Length + 14;
        var mfra = BmffModelFixtures.Container(new MovieFragmentRandomAccessBox(0, 0),
            new TrackFragmentRandomAccessBox(0, 0) { Version = 1, Entries = [
                new() { Time = 0, MoofOffset = firstOffset, TrafNumber = 1, TrunNumber = 1, SampleNumber = 1 },
                new() { Time = 6000, MoofOffset = secondOffset, TrafNumber = 1, TrunNumber = 1, SampleNumber = 1 }] },
            new MovieFragmentRandomAccessOffsetBox(0, 0));
        ((MovieFragmentRandomAccessOffsetBox)mfra.Children[1]).MfraSize = (uint)mfra.GetPayloadSize() + 8;
        using var document = new BmffDocument();
        foreach (var box in new BmffBox[] { brand, movie, first, new MediaDataBox(0, 0) { Payload = BinarySegment.Create(new byte[] { 1, 2, 3, 4, 5, 6 }) }, second, new MediaDataBox(0, 0) { Payload = BinarySegment.Create(new byte[] { 7, 8, 9, 10, 11, 12 }) }, mfra })
        {
            document.Boxes.Add(box);
        }
        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private static MovieFragmentBox Fragment(uint sequence, ulong time)
    {
        var run = new TrackFragmentRunBox(0, 0) { Version = 1, Flags = 0xf01, DataOffset = 0, Entries = [
            new() { Duration = 3000, Size = 3, Flags = 0x02000000, CompositionTimeOffset = 0 },
            new() { Duration = 3000, Size = 3, Flags = 0x02000000, CompositionTimeOffset = 0 }] };
        var result = BmffModelFixtures.Container(new MovieFragmentBox(0, 0), new MovieFragmentHeaderBox(0, 0) { SequenceNumber = sequence },
            BmffModelFixtures.Container(new TrackFragmentBox(0, 0), new TrackFragmentHeaderBox(0, 0) { Flags = 0x20000 },
                new TrackFragmentDecodeTimeBox { Version = 1, BaseMediaDecodeTime = time }, run));
        run.DataOffset = (int)result.GetPayloadSize() + 16;
        return result;
    }

    internal static byte[] Heif()
    {
        var brand = Brand("mif1");
        var location = new ItemLocationBox(0, 0) { Version = 2, Entries = [new() { ItemId = 1, Extents = [new() { Length = 6 }] }] };
        var meta = BmffModelFixtures.Container(new MetaBox(0, 0),
            new HandlerBox(0, 0) { HandlerType = FourCC.Parse("pict"), Name = "Synthetic image" },
            new PrimaryItemReferenceBox(0, 0) { ItemId = 1 },
            BmffModelFixtures.Container(new ItemInfoBox(0, 0), new ItemInfoEntryBox { ItemId = 1, ItemType = FourCC.Parse("hvc1"), ItemName = "Original fixture" }),
            location, BmffModelFixtures.Opaque("iprp", BmffFixtures.Box("zzzz", [42])));
        location.Entries[0].BaseOffset = (ulong)(BmffModelFixtures.Write(brand).Length + BmffModelFixtures.Write(meta).Length + 8);
        using var document = new BmffDocument();
        document.Boxes.Add(brand);
        document.Boxes.Add(meta);
        document.Boxes.Add(new MediaDataBox(0, 0) { Payload = BinarySegment.Create(new byte[] { 31, 32, 33, 34, 35, 36 }) });
        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private static FileTypeBox Brand(string brand) => new FileTypeBox(0) { MajorBrand = FourCC.Parse(brand).Value, CompatibleBrands = [FourCC.Parse(brand).Value] };

    private static void AddUnknowns(BmffBoxComposite parent)
    {
        foreach (var child in parent.Children)
        {
            if (child is BmffBoxComposite composite)
            {
                AddUnknowns(composite);
            }
        }
        parent.Children.Add(BmffModelFixtures.Opaque("zzzz", [0xde, 0xad]));
    }
}
