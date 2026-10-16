using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Heif.Tests;

public sealed class HeifInspectionFormatterTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Heif] - Inspection: ISO 23008-12 AVIF tree matches golden text")]
    public void Format_AvifFixture_MatchesGoldenText()
    {
        // Arrange: original fixtures are generated exclusively by this repository's BMFF writer.
        // This golden pins source coordinates, hierarchy, typed fields and opaque payload lengths.
        using var source = new MemoryStream(HeifFixtures.Create("avif"));
        using var document = BmffDocument.Load(source, registry: HeifBoxRegistry.Register(BmffBoxRegistry.CreateBuilder()).Build());
        source.Dispose();
        var expected = """
            ftyp @0x0 size=20 major=avif minor=0 brands=[mif1]
            meta @0x14 size=396 children=6 version=0 flags=0x000000
              hdlr @0x20 size=45 version=0 flags=0x000000 handler=pict
              pitm @0x4D size=14 version=0 flags=0x000000 item-id=1
              iinf @0x5B size=68 children=2 version=0 flags=0x000000 entries=2
                infe @0x69 size=27 version=2 flags=0x000000 item-id=1 item-type=av01
                infe @0x84 size=27 version=2 flags=0x000000 item-id=2 item-type=av01
              iloc @0x9F size=48 version=1 flags=0x000000 items=2
              iprp @0xCF size=183 children=2
                ipco @0xD7 size=145 children=6
                  ispe @0xDF size=20 version=0 flags=0x000000 dimensions=64x64
                  av1C @0xF3 size=12 lazy-payload=4
                  colr @0xFF size=19 lazy-payload=11 colour-type=nclx icc-bytes=0
                  pixi @0x112 size=16 version=0 flags=0x000000 channels=3
                  auxC @0x122 size=56 version=0 flags=0x000000 auxiliary-type=urn:mpeg:mpegB:cicp:systems:auxiliary:alpha
                  pixi @0x15A size=14 version=0 flags=0x000000 channels=1
                ipma @0x168 size=30 version=0 flags=0x000000 entries=2
              iref @0x186 size=26 children=1 version=0 flags=0x000000
                auxl @0x192 size=14 from-item-id=2 references=1
            mdat @0x1A0 size=16 lazy-payload=8
            """;

        // Act
        var actual = BmffTreeFormatter.Format(document);

        // Assert
        actual.ShouldBe(expected.ReplaceLineEndings("\n") + "\n");
    }
}
