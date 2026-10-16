using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Mpeg.Tests;

// Original multi-traf vector written by BMFF; exercises the previous-traf implicit base,
// tfhd over trex defaults, first-sample flags and a trun with no data_offset.
internal static class Mp4FragmentFixtures
{
    internal static byte[] ImplicitPreviousTrack()
    {
        var movie = Mp4Fixtures.Container(new MovieBox(0, 0), new MovieHeaderBox(0, 0),
            Mp4MediaFixtures.Track(1, true, 0, true), Mp4MediaFixtures.Track(2, false, 0, true),
            Mp4Fixtures.Container(new MovieExtensionBox(0, 0),
                new TrackExtensionBox(0, 0) { TrackId = 1, DefaultSampleDescriptionIndex = 1, DefaultSampleDuration = 77, DefaultSampleSize = 77, DefaultSampleFlags = 0x10000 },
                new TrackExtensionBox(0, 0) { TrackId = 2, DefaultSampleDescriptionIndex = 1, DefaultSampleDuration = 1000, DefaultSampleSize = 3 }));
        var firstRun = new TrackFragmentRunBox(0, 0)
        {
            Flags = 5, DataOffset = 0, FirstSampleFlags = 0x10000, Entries = [new(), new()]
        };
        var fragment = Mp4Fixtures.Container(new MovieFragmentBox(0, 0), new MovieFragmentHeaderBox(0, 0) { SequenceNumber = 1 },
            Mp4Fixtures.Container(new TrackFragmentBox(0, 0),
                new TrackFragmentHeaderBox(0, 0) { TrackId = 1, Flags = 0x38, DefaultSampleDuration = 1000, DefaultSampleSize = 3, DefaultSampleFlags = 0 },
                firstRun),
            Mp4Fixtures.Container(new TrackFragmentBox(0, 0), new TrackFragmentHeaderBox(0, 0) { TrackId = 2 },
                new TrackFragmentRunBox(0, 0) { Entries = [new(), new()] }));
        firstRun.DataOffset = (int)fragment.GetPayloadSize() + 16;
        return Mp4Fixtures.Write(Mp4Fixtures.Brand("iso6"), movie, fragment,
            new MediaDataBox(0, 0) { Payload = BinarySegment.Create(new byte[] { 1, 2, 3, 4, 5, 6, 21, 22, 23, 24, 25, 26 }) });
    }
}
