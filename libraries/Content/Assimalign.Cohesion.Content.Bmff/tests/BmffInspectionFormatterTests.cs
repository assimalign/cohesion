using System;
using System.Globalization;
using System.IO;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

public sealed class BmffInspectionFormatterTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Inspection: format 40 GiB without reading payloads")]
    public void Format_VirtualMedia_DoesNotReadOrSeekSource()
    {
        // Arrange: SparseMediaStream generates headers and rejects any read of the 40 GiB payload.
        using var source = new SparseMediaStream();
        using var document = BmffDocument.Load(source);
        var reads = source.BytesRead;
        var position = source.Position;

        // Act
        var text = BmffTreeFormatter.Format(document);

        // Assert
        text.ShouldBe("mdat @0x0 size=42949672976 [large-size] lazy-payload=42949672960\n" +
            "free @0xA00000010 size=8 lazy-payload=0\n");
        source.BytesRead.ShouldBe(reads);
        source.Position.ShouldBe(position);
        reads.ShouldBe(24);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Inspection: wire header markers and preserved types are explicit")]
    public void Format_HeaderVariants_ReportsLargeZeroUuidAndUnknown()
    {
        // Arrange: original bytes come from the local ISO 14496-12 header fixture writer.
        using var source = new MemoryStream(BmffFixtures.Join(BmffFixtures.Box("free", large: true),
            BmffFixtures.Box("zzzz"), BmffFixtures.Box("uuid", new byte[] { 1 }),
            BmffFixtures.Box("mdat", new byte[] { 1, 2, 3 }, zero: true)));
        using var document = BmffDocument.Load(source);
        source.Dispose(); // Inspection uses only the already loaded tree and segment lengths.

        // Act
        var text = BmffTreeFormatter.Format(document);

        // Assert
        text.ShouldBe("free @0x0 size=16 [large-size] lazy-payload=0\n" +
            "zzzz @0x10 size=8 [unknown preserved:Unsupported] lazy-payload=0\n" +
            "uuid @0x18 size=25 uuid=00112233-4455-6677-8899-aabbccddeeff [unknown preserved:Unsupported] lazy-payload=1\n" +
            "mdat @0x31 size=11 [size-zero] lazy-payload=3\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Inspection: unsupported versions are visibly preserved")]
    public void Format_UnsupportedVersion_ReportsPreservation()
    {
        // Arrange: the fixture writer emits an intentionally unsupported mvhd version.
        using var source = new MemoryStream(BmffFixtures.Box("mvhd", new byte[] { 2, 0, 0, 0 }));
        using var document = BmffDocument.Load(source);

        // Act / Assert
        BmffTreeFormatter.Format(document).ShouldBe("mvhd @0x0 size=12 [unknown preserved:Unsupported] lazy-payload=4\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Inspection: borrowed writers remain open and use invariant numbers")]
    public void Write_UnloadedFields_UsesInvariantTextWithoutMeasuring()
    {
        // Arrange
        var box = new TrackHeaderBox(0, 0) { TrackId = 7, Duration = 1234 };
        using var writer = new StringWriter(CultureInfo.GetCultureInfo("fr-FR")) { NewLine = "\n" };

        // Act
        BmffTreeFormatter.Write(box, writer);
        writer.Write("still open");

        // Assert
        writer.ToString().ShouldBe("tkhd @0x0 size=? [unloaded] version=0 flags=0x000000 track-id=7 duration=1234 dimensions=0x0\nstill open");
        BmffTreeFormatter.Format(new FecReservoirBox(0, 0)).ShouldContain("[unloaded]", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Inspection: hostile FourCCs stay on one physical line")]
    public void Format_ControlCharacters_EscapesThem()
    {
        // Arrange: raw FourCC bytes are legal even when not printable.
        using var source = new MemoryStream(BmffFixtures.Box("a\nb\0"));
        using var document = BmffDocument.Load(source);

        // Act / Assert
        BmffTreeFormatter.Format(document).ShouldBe("a\\x0Ab\\x00 @0x0 size=8 [unknown preserved:Unsupported] lazy-payload=0\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Inspection: formatter enforces traversal bounds")]
    public void Format_CycleOrDepthLimit_ThrowsContentError()
    {
        // Arrange
        var root = new MovieBox(0);
        var child = new MovieBox(0);
        root.Children.Add(child);

        // Act / Assert
        Should.Throw<ContentFormatException>(() => BmffTreeFormatter.Format(root, maxDepth: 0))
            .ErrorCode.ShouldBe(ContentFormatErrorCode.LimitExceeded);
        child.Children.Add(root);
        Should.Throw<ContentFormatException>(() => BmffTreeFormatter.Format(root))
            .ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
    }
}
