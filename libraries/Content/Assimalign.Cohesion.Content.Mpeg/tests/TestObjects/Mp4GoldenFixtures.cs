using System;
using System.IO;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Mpeg.Tests;

// Provenance: these five checked-in files are generated exclusively by the BMFF writer below.
// Placeholder bytes [1..12] stand in for AVC and [21..32] for AAC; no codec conformance is claimed.
// Free boxes align the video payload offsets in progressive and fragmented layouts so the entire
// MediaSample values (including byte coordinates) can be compared, not just their timestamps.
internal static class Mp4GoldenFixtures
{
    internal static byte[] Create(string name) => name switch
    {
        "progressive.mp4" => Progressive(),
        "fast-start.mp4" => FastStart(),
        "fragmented-cmaf.mp4" => Fragmented(),
        "audio-only.m4a" => Mp4MediaFixtures.Progressive(audioOnly: true),
        "delayed-video.mp4" => Delayed(),
        _ => throw new ArgumentException(nameof(name))
    };

    private static byte[] Progressive()
    {
        using var output = new MemoryStream();
        using var writer = BmffWriter.Create(output);
        writer.Write(Mp4Fixtures.Brand("mp42"));
        Pad(output, writer, 4096 - 8);
        writer.Write(new MediaDataBox(0, 0) { Payload = BinarySegment.Create(new byte[] { 1, 2, 3, 4, 5, 6 }) });
        Pad(output, writer, 8192 - 8);
        writer.Write(new MediaDataBox(0, 0) { Payload = BinarySegment.Create(new byte[] { 7, 8, 9, 10, 11, 12 }) });
        var audioOffset = (uint)output.Position + 8;
        writer.Write(new MediaDataBox(0, 0) { Payload = BinarySegment.Create(new byte[] { 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 }) });
        var video = Mp4MediaFixtures.Track(1, true, 4096);
        Mp4MediaFixtures.Find<SampleToChunkBox>(video).Entries = [new() { FirstChunk = 1, SamplesPerChunk = 2, SampleDescriptionIndex = 1 }];
        Mp4MediaFixtures.Find<ChunkOffsetBox>(video).Entries = [4096, 8192];
        var movie = Mp4Fixtures.Container(new MovieBox(0, 0),
            new MovieHeaderBox(0, 0) { TimeScale = 1000, Duration = 4000, NextTrackId = 3 },
            video, Mp4MediaFixtures.Track(2, false, audioOffset));
        writer.Write(movie);
        return output.ToArray();
    }

    private static byte[] Fragmented()
    {
        using var output = new MemoryStream();
        using var writer = BmffWriter.Create(output);
        writer.Write(Mp4Fixtures.Brand("cmfc"));
        writer.Write(Mp4Fixtures.Container(new MovieBox(0, 0),
            new MovieHeaderBox(0, 0) { TimeScale = 1000, NextTrackId = 2 },
            Mp4MediaFixtures.Track(1, true, 0, true),
            Mp4Fixtures.Container(new MovieExtensionBox(0, 0),
                new TrackExtensionBox(0, 0) { TrackId = 1, DefaultSampleDescriptionIndex = 1, DefaultSampleDuration = 1000, DefaultSampleSize = 3 })));
        var first = Mp4MediaFixtures.Fragment(1, 0, true);
        Pad(output, writer, 4096 - 8 - Mp4Fixtures.Write(first).Length);
        writer.Write(first);
        writer.Write(new MediaDataBox(0, 0) { Payload = BinarySegment.Create(new byte[] { 1, 2, 3, 4, 5, 6 }) });
        var second = Mp4MediaFixtures.Fragment(2, 2000, true);
        Pad(output, writer, 8192 - 8 - Mp4Fixtures.Write(second).Length);
        writer.Write(second);
        writer.Write(new MediaDataBox(0, 0) { Payload = BinarySegment.Create(new byte[] { 7, 8, 9, 10, 11, 12 }) });
        return output.ToArray();
    }

    private static byte[] FastStart()
    {
        using var source = new MemoryStream(Progressive());
        using var document = Mp4Profile.LoadDocument(source);
        Mp4Profile.FastStart(document);
        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private static byte[] Delayed()
    {
        using var source = new MemoryStream(Progressive());
        using var document = Mp4Profile.LoadDocument(source);
        var movie = (MovieBox)document.Boxes[document.Boxes.Count - 1];
        var track = Mp4MediaFixtures.Find<TrackBox>(movie);
        track.Children.Add(Mp4Fixtures.Container(new EditBox(0, 0), new EditListBox(0, 0)
        {
            Entries = [new() { SegmentDuration = 2000, MediaTime = -1, MediaRateInteger = 1 },
                new() { SegmentDuration = 4000, MediaTime = 0, MediaRateInteger = 1 }]
        }));
        Mp4MediaFixtures.Find<TrackHeaderBox>(track).Duration = 6000;
        Mp4MediaFixtures.Find<MovieHeaderBox>(movie).Duration = 6000;
        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private static void Pad(MemoryStream output, BmffWriter writer, long position)
    {
        var length = checked((int)(position - output.Position - 8));
        if (length < 0) { throw new InvalidOperationException(); }
        writer.Write(new FreeSpaceBox(0, 0) { Payload = BinarySegment.Create(new byte[length]) });
    }
}
