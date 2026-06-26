using System;
using Assimalign.Cohesion.Http.Internal;
using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpMethodTests
{
    [Theory]
    [InlineData("get", "GET")]
    [InlineData("pOSt", "POST")]
    [InlineData("PUT", "PUT")]
    [InlineData("COnnECT", "CONNECT")]
    public void Constructor_MixedCaseInput_ShouldNormalizeToUpperInvariant(string value, string expected)
    {
        // Arrange
        HttpMethod method = value;

        // Act
        string actual = method.Value;

        // Assert
        actual.ShouldBe(expected);
    }

    [Fact]
    public void GetCanonicalizedValue_StandardMethod_ShouldReturnEqualValue()
    {
        // Arrange
        const string method = "get";

        // Act
        HttpMethod actual = HttpMethod.GetCanonicalizedValue(method);

        // Assert
        actual.ShouldBe(HttpMethod.Get);
    }

    [Fact]
    public void Constructor_InvalidCharacter_ShouldThrowHttpException()
    {
        // Arrange
        const string method = "GE T";

        // Act
        Action action = () => _ = new HttpMethod(method);

        // Assert
        action.ShouldThrow<ArgumentException>();
    }
}
