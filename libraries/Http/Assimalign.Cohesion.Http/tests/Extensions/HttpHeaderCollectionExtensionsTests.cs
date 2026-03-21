using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpHeaderCollectionExtensionsTests
{
    [Fact]
    public void GetValue_MissingHeader_ShouldReturnNull()
    {
        // Arrange
        HttpHeaderCollection headers = new();

        // Act
        string? value = headers.GetValue(HttpHeaderKey.ContentType);

        // Assert
        value.ShouldBeNull();
    }

    [Fact]
    public void SetValue_EmptyValue_ShouldRemoveHeader()
    {
        // Arrange
        HttpHeaderCollection headers = new();
        headers.SetValue(HttpHeaderKey.ContentType, "text/plain");

        // Act
        headers.SetValue(HttpHeaderKey.ContentType, string.Empty);

        // Assert
        headers.ContainsKey(HttpHeaderKey.ContentType).ShouldBeFalse();
    }

    [Fact]
    public void AppendValue_ExistingHeader_ShouldUseConsistentCommaSeparatedSerialization()
    {
        // Arrange
        HttpHeaderCollection headers = new();

        // Act
        headers.AppendValue(HttpHeaderKey.Accept, "text/plain");
        headers.AppendValue(HttpHeaderKey.Accept, "application/json");

        // Assert
        headers.GetValue(HttpHeaderKey.Accept).ShouldBe("text/plain,application/json");
        headers[HttpHeaderKey.Accept].Value.ShouldBe("text/plain,application/json");
        headers[HttpHeaderKey.Accept].ToString().ShouldBe("text/plain,application/json");
    }
}
