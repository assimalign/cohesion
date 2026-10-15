using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Security.DataProtection.Tests;

public class KeyDocumentNameTests
{
    [Theory(DisplayName = "Cohesion Test [Security.DataProtection] - KeyDocumentName: Should refuse invalid names without throwing")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("prefix..suffix")]
    [InlineData("sub/key")]
    [InlineData("/rooted")]
    [InlineData("bad\0name")]
    public void TryCreate_InvalidName_ShouldReturnFalse(string? value)
    {
        // Arrange / Act
        bool created = KeyDocumentName.TryCreate(value, out KeyDocumentName name);

        // Assert
        created.ShouldBeFalse();
        name.ShouldBe(default);
    }

    [Theory(DisplayName = "Cohesion Test [Security.DataProtection] - KeyDocumentName: Should preserve the platform filename rule")]
    [InlineData("sub\\key")]
    [InlineData("C:relative")]
    [InlineData("bad?name")]
    public void TryCreate_PlatformSpecificName_ShouldPreserveRepositoryRule(string value)
    {
        // Arrange
        bool expected = !OperatingSystem.IsWindows();

        // Act
        bool created = KeyDocumentName.TryCreate(value, out KeyDocumentName name);

        // Assert
        created.ShouldBe(expected);
        if (created)
        {
            name.Value.ShouldBe(value);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - KeyDocumentName: Should preserve exact spelling and ordinal identity")]
    public void Equals_CaseVariants_ShouldRemainDistinct()
    {
        // Arrange
        KeyDocumentName upper = "Key-123";
        KeyDocumentName lower = "key-123";

        // Act
        bool equal = upper.Equals(lower);

        // Assert
        equal.ShouldBeFalse();
        upper.Value.ShouldBe("Key-123");
        upper.CompareTo(lower).ShouldBeLessThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - KeyDocument: Should refuse a default name")]
    public void Constructor_DefaultName_ShouldThrow()
    {
        // Arrange
        KeyDocumentName name = default;

        // Act
        Action construct = () => _ = new KeyDocument(name, ReadOnlyMemory<byte>.Empty);

        // Assert
        Should.Throw<ArgumentException>(construct).ParamName.ShouldBe("name");
    }

    [Fact(DisplayName = "Cohesion Test [Security.DataProtection] - KeyDocument: Should retain a validated name and opaque content")]
    public void Constructor_ValidName_ShouldPreserveNameAndContent()
    {
        // Arrange
        KeyDocumentName name = "key-123";
        byte[] content = [1, 2, 3];

        // Act
        KeyDocument document = new(name, content);

        // Assert
        document.Name.ShouldBe(name);
        document.Content.ToArray().ShouldBe(content);
    }
}
