using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Security.DataProtection.Tests;

public class KeyDocumentTests
{
    [Theory(DisplayName = "Cohesion Test [Security.DataProtection] - KeyDocument: Should preserve null and empty name rejection")]
    [InlineData(null)]
    [InlineData("")]
    public void Constructor_MissingName_ShouldThrowArgumentException(string? name)
    {
        // Arrange
        ReadOnlyMemory<byte> content = ReadOnlyMemory<byte>.Empty;

        // Act
        Action construct = () => _ = new KeyDocument(name!, content);

        // Assert
        Should.Throw<ArgumentException>(construct).ShouldBeOfType<ArgumentException>();
    }

    [Theory(DisplayName = "Cohesion Test [Security.DataProtection] - KeyDocument: Should refuse unsafe names before storage")]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("prefix..suffix")]
    [InlineData("sub/key")]
    [InlineData("bad\0name")]
    public void Constructor_UnsafeName_ShouldThrowBeforeStorage(string name)
    {
        // Arrange
        ReadOnlyMemory<byte> content = new byte[] { 1, 2, 3 };

        // Act
        Action construct = () => _ = new KeyDocument(name, content);

        // Assert
        Should.Throw<ArgumentException>(construct);
    }
}
