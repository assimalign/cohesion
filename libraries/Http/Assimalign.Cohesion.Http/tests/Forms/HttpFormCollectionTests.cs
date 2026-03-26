using System.IO;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpFormCollectionTests
{
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
