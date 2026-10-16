using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Mpeg.Tests;

public sealed class Mp4InspectionFormatterTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Mpeg] - Inspection: progressive MP4 tree matches golden text")]
    public void Format_ProgressiveMp4Fixture_MatchesGoldenText()
    {
        // Arrange: original fixtures are generated exclusively by this repository's BMFF writer.
        // This golden pins source coordinates, hierarchy, typed fields and opaque payload lengths.
        using var source = new MemoryStream(Mp4GoldenFixtures.Create("progressive.mp4"));
        using var document = Mp4Profile.LoadDocument(source);
        source.Dispose();
        var expected = """
            ftyp @0x0 size=24 major=mp42 minor=0 brands=[isom,mp42]
            free @0x18 size=4064 lazy-payload=4056
            mdat @0xFF8 size=14 lazy-payload=6
            free @0x1006 size=4082 lazy-payload=4074
            mdat @0x1FF8 size=14 lazy-payload=6
            mdat @0x2006 size=20 lazy-payload=12
            moov @0x201A size=982 children=3
              mvhd @0x2022 size=108 version=0 flags=0x000000 timescale=1000 duration=4000 next-track-id=3
              trak @0x208E size=459 children=2
                tkhd @0x2096 size=92 version=0 flags=0x000003 track-id=1 duration=4000 dimensions=0x0
                mdia @0x20F2 size=359 children=3
                  mdhd @0x20FA size=32 version=0 flags=0x000000 timescale=1000 duration=4000
                  hdlr @0x211A size=33 version=0 flags=0x000000 handler=vide
                  minf @0x213B size=286 children=2
                    dinf @0x2143 size=36 children=1
                      dref @0x214B size=28 children=1 version=0 flags=0x000000 entries=1
                        url  @0x215B size=12 version=0 flags=0x000001
                    stbl @0x2167 size=242 children=6
                      stsd @0x216F size=114 children=1 version=0 flags=0x000000 entries=1
                        avc1 @0x217F size=98 children=1 dimensions=1920x1080 data-reference=1
                          avcC @0x21D5 size=12 lazy-payload=4
                      stts @0x21E1 size=24 version=0 flags=0x000000 entries=1
                      stsc @0x21F9 size=28 version=0 flags=0x000000 entries=1
                      stsz @0x2215 size=20 version=0 flags=0x000000 samples=4 sample-size=3
                      stco @0x2229 size=24 version=0 flags=0x000000 entries=2
                      stss @0x2241 size=24 version=0 flags=0x000000 entries=2
              trak @0x2259 size=407 children=2
                tkhd @0x2261 size=92 version=0 flags=0x000003 track-id=2 duration=4000 dimensions=0x0
                mdia @0x22BD size=307 children=3
                  mdhd @0x22C5 size=32 version=0 flags=0x000000 timescale=1000 duration=4000
                  hdlr @0x22E5 size=33 version=0 flags=0x000000 handler=soun
                  minf @0x2306 size=234 children=2
                    dinf @0x230E size=36 children=1
                      dref @0x2316 size=28 children=1 version=0 flags=0x000000 entries=1
                        url  @0x2326 size=12 version=0 flags=0x000001
                    stbl @0x2332 size=190 children=6
                      stsd @0x233A size=66 children=1 version=0 flags=0x000000 entries=1
                        mp4a @0x234A size=50 children=1 channels=2 sample-rate=48000 data-reference=1
                          esds @0x236E size=14 lazy-payload=6 full-box-prefix=lazy
                      stts @0x237C size=24 version=0 flags=0x000000 entries=1
                      stsc @0x2394 size=28 version=0 flags=0x000000 entries=1
                      stsz @0x23B0 size=20 version=0 flags=0x000000 samples=4 sample-size=3
                      stco @0x23C4 size=20 version=0 flags=0x000000 entries=1
                      stss @0x23D8 size=24 version=0 flags=0x000000 entries=2
            """;

        // Act
        var actual = BmffTreeFormatter.Format(document);

        // Assert
        actual.ShouldBe(expected.ReplaceLineEndings("\n") + "\n");
    }
}
