using System.IO;
using System.Linq;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpFormCollectionTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Forms] - HttpFormCollection: repeated Add preserves all values and reads first")]
    public void Add_RepeatedField_ShouldAppendEveryValueAndKeepFirst()
    {
        // Arrange
        HttpFormCollection form = new();
        form.Add("tag", "first");
        HttpQueryValue earlier = form["tag"];

        // Act
        form.Add("TAG", HttpQueryValue.Concat("", "last"));
        bool found = form.TryGetValue("tag", out HttpQueryValue values);

        // Assert
        found.ShouldBeTrue();
        form.Count.ShouldBe(1);
        values.ToArray().ShouldBe(new[] { "first", "", "last" });
        values.Value.ShouldBe("first");
        earlier.ToArray().ShouldBe(new[] { "first" });
        form.Single().Value.ShouldBe(values);
    }

    [Fact]
    public void Add_ValueAndFile_ShouldBeRetrievableFromCollection()
    {
        // Arrange
        HttpFormCollection form = new();
        HttpFormFile file = new("avatar", "avatar.png", () => new MemoryStream([1, 2, 3]), 3, "image/png");

        // Act
        form.Add("name", "cohesion");
        form.Add(file);
        bool foundValue = form.TryGetValue("name", out HttpQueryValue value);
        bool foundFile = form.Files.TryGetValue("avatar", out HttpFormFile uploadedFile);

        // Assert
        foundValue.ShouldBeTrue();
        value.Value.ShouldBe("cohesion");
        foundFile.ShouldBeTrue();
        uploadedFile.FileName.ShouldBe("avatar.png");
        uploadedFile.ContentType.ShouldBe("image/png");

        using Stream stream = uploadedFile.OpenReadStream();
        stream.Length.ShouldBe(3);
    }
}
