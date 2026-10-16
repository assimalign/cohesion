using System;
using System.IO;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Mpeg.Tests;

// Original container-only fixtures generated with the BMFF writer. All codec bytes are placeholders.
internal static class Mp4MediaFixtures
{
    internal static byte[] Progressive(bool delay = false, bool audioOnly = false, bool compact = false, bool wide = false, bool reorder = false, uint samples = 4)
    {
        var brand = Mp4Fixtures.Brand(audioOnly ? "M4A " : "mp42");
        var dataOffset = (uint)Mp4Fixtures.Write(brand).Length + 8;
        var track = Track(1, !audioOnly, dataOffset, false, samples, compact, wide, reorder);
        var movie = Mp4Fixtures.Container(new MovieBox(0, 0), new MovieHeaderBox(0, 0) { TimeScale = 1000, Duration = samples * 1000 + (delay ? 2000u : 0u) }, track);
        if (delay)
        {
            track.Children.Add(Mp4Fixtures.Container(new EditBox(0, 0), new EditListBox(0, 0)
            {
                Entries = [new() { SegmentDuration = 2000, MediaTime = -1, MediaRateInteger = 1 },
                    new() { SegmentDuration = samples * 1000, MediaTime = 0, MediaRateInteger = 1 }]
            }));
        }
        var payload = new byte[samples * 3];
        for (var i = 0; i < payload.Length; i++) { payload[i] = (byte)(i + 1); }
        return Mp4Fixtures.Write(brand, new MediaDataBox(0, 0) { Payload = BinarySegment.Create(payload) }, movie);
    }

    internal static TrackBox Track(uint id, bool video, uint offset, bool fragmented = false, uint count = 4, bool compact = false, bool wide = false, bool reorder = false)
    {
        BmffBoxComposite entry = video ?
            Mp4Fixtures.Container(new Avc1SampleEntryBox { Width = 1920, Height = 1080, CompressorName = "Cohesion fixture" },
                new AvcConfigurationBox { Data = BinarySegment.Create(new byte[] { 1, 0x64, 0, 0x1f }) }) :
            Mp4Fixtures.Container(new Mp4aSampleEntryBox { ChannelCount = 2, SampleRate = 48000 },
                new ElementaryStreamDescriptorBox { Data = BinarySegment.Create(new byte[] { 0, 0, 0, 0, 3, 0 }) });
        var sizes = new ushort[fragmented ? 0 : count];
        Array.Fill(sizes, (ushort)3);
        var table = Mp4Fixtures.Container(new SampleTableBox(0, 0),
            Mp4Fixtures.Container(new SampleDescriptionBox(0, 0), entry),
            new SampleDecodingTimeBox(0, 0) { Entries = fragmented ? [] : [new() { SampleCount = count, SampleDelta = 1000 }] },
            new SampleToChunkBox(0, 0) { Entries = fragmented ? [] : [new() { FirstChunk = 1, SamplesPerChunk = count, SampleDescriptionIndex = 1 }] },
            compact ? new CompactSampleSizeBox(0, 0) { FieldSize = 4, Entries = sizes } :
                new SampleSizeBox(0, 0) { SampleSize = 3, SampleCount = fragmented ? 0 : count },
            wide ? new ChunkOffset64BitBox(0, 0) { Entries = fragmented ? [] : [offset] } : new ChunkOffsetBox(0, 0) { Entries = fragmented ? [] : [offset] });
        if (!fragmented)
        {
            table.Children.Add(new SyncSampleTableBox(0, 0) { Entries = count >= 3 ? [1, 3] : [1] });
            if (reorder)
            {
                table.Children.Add(new SampleCompositionTimeBox(0, 0)
                {
                    Version = 1, Entries = [new() { SampleCount = 1, SampleOffset = 0 }, new() { SampleCount = 1, SampleOffset = 1000 },
                        new() { SampleCount = 1, SampleOffset = -1000 }, new() { SampleCount = 1, SampleOffset = 0 }]
                });
            }
        }
        var info = Mp4Fixtures.Container(new MediaInfoBox(0, 0),
            Mp4Fixtures.Container(new DataInfoBox(0, 0), Mp4Fixtures.Container(new DataReferenceBox(0, 0), new DataEntryUrlBox { Flags = 1 })),
            table);
        return Mp4Fixtures.Container(new TrackBox(0, 0),
            new TrackHeaderBox(0, 0) { TrackId = id, Flags = 3, Duration = fragmented ? 0 : count * 1000 },
            Mp4Fixtures.Container(new MediaBox(0, 0),
                new MediaHeaderBox(0, 0) { TimeScale = 1000, Duration = fragmented ? 0 : count * 1000, PackedLanguage = 0x15c7 },
                new HandlerBox(0, 0) { HandlerType = FourCC.Parse(video ? "vide" : "soun") }, info));
    }

    internal static byte[] Fragmented(bool defaults = true, bool implicitBase = false, bool explicitBase = false)
    {
        var brand = Mp4Fixtures.Brand(implicitBase || explicitBase ? "iso6" : "cmfc");
        var track = Track(1, true, 0, true);
        var movie = Mp4Fixtures.Container(new MovieBox(0, 0), new MovieHeaderBox(0, 0) { TimeScale = 1000 },
            track, Mp4Fixtures.Container(new MovieExtensionBox(0, 0),
                new TrackExtensionBox(0, 0) { TrackId = 1, DefaultSampleDescriptionIndex = 1, DefaultSampleDuration = 1000, DefaultSampleSize = 3, DefaultSampleFlags = 0 }));
        var first = Fragment(1, 0, defaults);
        var second = Fragment(2, 2000, defaults);
        var position = Mp4Fixtures.Write(brand, movie).Length;
        foreach (var fragment in new[] { first, second })
        {
            var traf = (TrackFragmentBox)fragment.Children[1];
            var tfhd = (TrackFragmentHeaderBox)traf.Children[0];
            var run = (TrackFragmentRunBox)traf.Children[2];
            if (implicitBase) { tfhd.Flags = 0; }
            if (explicitBase)
            {
                tfhd.Flags = 1;
                tfhd.BaseDataOffset = (ulong)position;
            }
            run.DataOffset = (int)fragment.GetPayloadSize() + 16;
            position += Mp4Fixtures.Write(fragment).Length + 14;
        }
        return Mp4Fixtures.Write(brand, movie, first,
            new MediaDataBox(0, 0) { Payload = BinarySegment.Create(new byte[] { 1, 2, 3, 4, 5, 6 }) }, second,
            new MediaDataBox(0, 0) { Payload = BinarySegment.Create(new byte[] { 7, 8, 9, 10, 11, 12 }) });
    }

    internal static MovieFragmentBox Fragment(uint sequence, ulong decodeTime, bool defaults)
    {
        var run = new TrackFragmentRunBox(0, 0)
        {
            Flags = defaults ? 0x401u : 0x701u, DataOffset = 0,
            Entries = [new() { Duration = 1000, Size = 3, Flags = 0 }, new() { Duration = 1000, Size = 3, Flags = 0x10000 }]
        };
        var fragment = Mp4Fixtures.Container(new MovieFragmentBox(0, 0), new MovieFragmentHeaderBox(0, 0) { SequenceNumber = sequence },
            Mp4Fixtures.Container(new TrackFragmentBox(0, 0), new TrackFragmentHeaderBox(0, 0) { TrackId = 1, Flags = 0x20000 },
                new TrackFragmentDecodeTimeBox { BaseMediaDecodeTime = decodeTime }, run));
        run.DataOffset = (int)fragment.GetPayloadSize() + 16;
        return fragment;
    }

    internal static T Find<T>(BmffBoxComposite parent) where T : BmffBox
    {
        foreach (var child in parent.Children)
        {
            if (child is T result) { return result; }
            if (child is BmffBoxComposite composite)
            {
                var nested = FindOrNull<T>(composite);
                if (nested is not null) { return nested; }
            }
        }
        throw new InvalidOperationException();
    }
    private static T? FindOrNull<T>(BmffBoxComposite parent) where T : BmffBox
    {
        foreach (var child in parent.Children)
        {
            if (child is T result) { return result; }
            if (child is BmffBoxComposite composite)
            {
                var result2 = FindOrNull<T>(composite);
                if (result2 is not null) { return result2; }
            }
        }
        return null;
    }
}
