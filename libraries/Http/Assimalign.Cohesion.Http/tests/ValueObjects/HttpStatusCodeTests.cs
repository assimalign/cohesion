using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpStatusCodeTests
{
    [Theory]
    [InlineData(200, "200 Ok")]
    [InlineData(300, "300 Multiple Choices")]
    [InlineData(425, "425 Too Early")]
    public void ToString_KnownStatusCode_ShouldReturnCodeAndReasonPhrase(int value, string expected)
    {
        // Arrange
        HttpStatusCode statusCode = value;

        // Act
        string actual = statusCode.ToString();

        // Assert
        actual.ShouldBe(expected);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(300, true)]
    [InlineData(425, true)]
    [InlineData(999, false)]
    public void IsValid_KnownAndUnknownCodes_ShouldReturnExpectedResult(int value, bool expected)
    {
        // Act
        bool actual = HttpStatusCode.IsValid(value);

        // Assert
        actual.ShouldBe(expected);
    }

    [Fact]
    public void ImplicitConversions_ShouldRoundTripNumericValue()
    {
        // Arrange
        HttpStatusCode statusCode = HttpStatusCode.NoContent;

        // Act
        int numericValue = statusCode;

        // Assert
        numericValue.ShouldBe(204);
        ((HttpStatusCode)numericValue).ShouldBe(HttpStatusCode.NoContent);
    }
}
