using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpPathTests
{
    [Fact]
    public void Constructor_EmptyValue_ShouldNormalizeToRoot()
    {
        // Arrange
        const string value = "";

        // Act
        HttpPath path = new(value);

        // Assert
        path.Value.ShouldBe("/");
        path.ShouldBe(HttpPath.Root);
    }

    [Fact]
    public void Concat_RootAndChildPath_ShouldCombineWithoutDuplicateSeparator()
    {
        // Arrange
        HttpPath root = HttpPath.Root;
        HttpPath child = new("/api");

        // Act
        HttpPath combined = root.Concat(child);

        // Assert
        combined.Value.ShouldBe("/api");
    }

    [Fact]
    public void FromUriComponent_EncodedPath_ShouldDecodePercentEncodedSegments()
    {
        // Arrange
        const string component = "/hello%24world";

        // Act
        HttpPath decoded = HttpPath.FromUriComponent(component);

        // Assert
        decoded.Value.ShouldBe("/hello$world");
    }

    [Fact]
    public void Constructor_InvalidCharacter_ShouldThrowHttpException()
    {
        // Arrange
        const string value = "/hello world";

        // Act
        Action action = () => _ = new HttpPath(value);

        // Assert
        action.ShouldThrow<HttpException>().Code.ShouldBe(HttpErrorCode.InvalidPath);
    }
}
