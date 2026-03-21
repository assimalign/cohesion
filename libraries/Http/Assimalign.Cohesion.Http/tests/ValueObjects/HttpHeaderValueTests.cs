using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpHeaderValueTests
{
    [Fact]
    public void Value_MultipleValues_ShouldMatchStringConversionAndToString()
    {
        // Arrange
        HttpHeaderValue value = new(new[] { "text/plain", "application/json" });

        // Act
        string implicitValue = value;

        // Assert
        value.Value.ShouldBe("text/plain,application/json");
        implicitValue.ShouldBe(value.Value);
        value.ToString().ShouldBe(value.Value);
    }

    [Fact]
    public void Value_NullAndEmptyEntries_ShouldSkipEmptySegments()
    {
        // Arrange
        HttpHeaderValue value = new(new string?[] { "text/plain", null, string.Empty, "application/json" });

        // Act
        string actual = value.Value;

        // Assert
        actual.ShouldBe("text/plain,application/json");
    }

    [Fact]
    public void Value_EmptyInstance_ShouldReturnEmptyStringAcrossAccessors()
    {
        // Arrange
        HttpHeaderValue value = HttpHeaderValue.Empty;

        // Act
        string implicitValue = value;

        // Assert
        value.Value.ShouldBe(string.Empty);
        implicitValue.ShouldBe(string.Empty);
        value.ToString().ShouldBe(string.Empty);
    }
}
