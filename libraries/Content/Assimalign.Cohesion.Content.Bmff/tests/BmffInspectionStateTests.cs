using System.IO;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

public sealed class BmffInspectionStateTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Inspection: original coordinates survive editing and saving")]
    public void Offset_AfterEditAndSave_RetainsSourceCoordinates()
    {
        // Arrange: original bytes are made by the local fixture writer.
        using var source = new MemoryStream(BmffFixtures.Box("moov", BmffFixtures.Box("free")));
        using var document = BmffDocument.Load(source);
        var movie = (MovieBox)document.Boxes[0];
        var child = movie.Children[0];
        movie.Children.Insert(0, new FreeSpaceBox(0, 0));

        // Act
        using var output = new MemoryStream();
        document.Save(output);

        // Assert
        child.Offset.ShouldBe(8);
        child.Limit.ShouldBe(8);
        movie.Limit.ShouldBe(16);
        output.Length.ShouldBe(24);
        movie.IsComposite.ShouldBeTrue();
        movie.IsLeaf.ShouldBeFalse();
        child.IsLeaf.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Bmff] - Inspection: unloaded boxes retain constructor state")]
    public void Limit_UnloadedBox_RetainsConstructorValuesAfterWriting()
    {
        // Arrange
        var movie = new MovieBox(123, 456);
        using var output = new MemoryStream();
        using var writer = BmffWriter.Create(output);

        // Act
        writer.Write(movie);

        // Assert
        movie.Offset.ShouldBe(123);
        movie.Limit.ShouldBe(456);
        movie.Header.HeaderSize.ShouldBe(0);
        movie.IsComposite.ShouldBeTrue();
        movie.IsLeaf.ShouldBeFalse();
        output.Length.ShouldBe(8);
        new MovieBox(0).Limit.ShouldBe(0);
    }
}
