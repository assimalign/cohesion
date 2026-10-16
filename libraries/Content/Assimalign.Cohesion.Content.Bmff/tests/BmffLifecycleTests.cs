using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

public class BmffLifecycleTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Document trees round trip through borrowed sources")]
    public void Save_NestedDocument_PreservesBytesAndOwnership()
    {
        // Arrange: this fixture is produced entirely by the BMFF writer.
        using var original = new BmffDocument();
        var movie = new MovieBox(0);
        movie.Children.Add(new TrackBox(size: 0, offset: 0));
        original.Boxes.Add(new FileTypeBox(0) { MajorBrand = FourCC.Parse("isom").Value });
        original.Boxes.Add(movie);
        original.Boxes.Add(new MediaDataBox(0, 0) { Payload = BinarySegment.Create(new byte[] { 1, 2, 3 }) });
        using var source = new MemoryStream();
        original.Save(source);
        var expected = source.ToArray();
        source.Position = 0;

        // Act
        using var document = BmffDocument.Load(source);
        using var output = new MemoryStream();
        document.Save(output);
        document.Dispose();

        // Assert
        output.ToArray().ShouldBe(expected);
        ((MediaDataBox)document.Boxes[2]).Payload.IsMaterialized.ShouldBeFalse();
        source.CanRead.ShouldBeTrue();
        ((MovieBox)document.Boxes[1]).Children.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Asynchronous save copies lazy payloads")]
    public async Task SaveAsync_LazyDocument_PreservesBytes()
    {
        // Arrange
        using var source = new MemoryStream(new byte[] { 0, 0, 0, 11, 0x6d, 0x64, 0x61, 0x74, 1, 2, 3 });
        using var document = await BmffDocument.LoadAsync(source, cancellationToken: CancellationToken.None);
        using var output = new MemoryStream();
        // Act
        await document.SaveAsync(output, CancellationToken.None);
        // Assert
        output.ToArray().ShouldBe(source.ToArray());
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Streaming containers compute supplied and buffered sizes")]
    public void EndContainer_BufferedAndSupplied_ProducesSameBytes()
    {
        // Arrange
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        using var supplied = BmffWriter.Create(first);
        using var buffered = BmffWriter.Create(second);
        var free = new FreeSpaceBox(0, 0);
        // Act
        supplied.BeginContainer(BmffBoxType.Movie, 8);
        supplied.Write(free);
        supplied.EndContainer();
        buffered.BeginContainer(BmffBoxType.Movie);
        buffered.Write(free);
        buffered.EndContainer();
        // Assert
        first.ToArray().ShouldBe(second.ToArray());
        first.Length.ShouldBe(16);
    }
}
