using System.Globalization;
using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

public sealed class BmffInspectionDebuggerTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Inspection: debugger display uses source headers and live child counts")]
    public void DebuggerDisplay_EditedComposite_UsesSourceSizeAndCurrentShape()
    {
        // Arrange: source coordinates include the 32-byte prefix before the first parsed box.
        using var source = new MemoryStream(BmffFixtures.Join(new byte[32],
            BmffFixtures.Box("moov", BmffFixtures.Join(BmffFixtures.Box("free", new byte[1180]), BmffFixtures.Box("free")))));
        source.Position = 32;
        using var document = BmffDocument.Load(source);
        var movie = document.First<MovieBox>();
        movie.Children.Add(new FreeSpaceBox(0, 0));

        // Act / Assert: call exactly the member named by DebuggerDisplayAttribute, without reflection.
        movie.DebuggerDisplay.ShouldBe("moov @0x20 size 1,204 [3 children]");
        var proxy = new BmffBoxCompositeDebugView(movie);
        proxy.Children.ShouldBeSameAs(movie.Children);
        proxy.Children.Count.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Inspection: debugger display keeps 40 GiB lazy")]
    public void DebuggerDisplay_VirtualMedia_DoesNotReadPayload()
    {
        // Arrange
        using var source = new SparseMediaStream();
        using var document = BmffDocument.Load(source);
        var reads = source.BytesRead;
        var media = document.First<MediaDataBox>();

        // Act / Assert
        media.DebuggerDisplay.ShouldBe("mdat @0x0 size 40 GiB (lazy)");
        source.BytesRead.ShouldBe(reads);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Inspection: debugger display is invariant and handles unloaded boxes")]
    public void DebuggerDisplay_UnloadedBox_UsesConstructorCoordinatesWithoutMeasuring()
    {
        // Arrange
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var box = new MovieBox(32, 1204);

            // Act / Assert
            box.DebuggerDisplay.ShouldBe("moov @0x20 size 1,204 [0 children] [unloaded]");
            new FecReservoirBox(0, 0).DebuggerDisplay.ShouldBe("fecr @0x0 size ? [unloaded]");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Inspection: unknown debugger nodes expose their raw identity")]
    public void DebuggerDisplay_UnknownBox_ReportsPreservedLazyShape()
    {
        // Arrange
        using var source = new MemoryStream(BmffFixtures.Box("zzzz", new byte[] { 1 }));
        using var document = BmffDocument.Load(source);
        source.Dispose();

        // Act / Assert
        document.First<UnknownBox>().DebuggerDisplay.ShouldBe("zzzz @0x0 size 9 (lazy) [preserved]");
    }
}
