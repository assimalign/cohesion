using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public sealed class ApplicationCredentialRegressionTests
{
    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Credential construction rejects invalid HTTP authentication schemes")]
    [InlineData("Bad Scheme")]
    [InlineData("Bad\tScheme")]
    [InlineData("Bad\rScheme")]
    [InlineData("Bad\nScheme")]
    [InlineData("Bearer:")]
    [InlineData("Béarer")]
    public void Constructor_InvalidScheme_ShouldRejectAtIssuance(string scheme)
    {
        // Arrange & Act
        Action create = () => _ = new ApplicationCredential(scheme, "opaque-secret", DateTimeOffset.UnixEpoch);

        // Assert
        Should.Throw<ArgumentException>(create);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Credential construction rejects multiline values")]
    [InlineData("opaque\rsecret")]
    [InlineData("opaque\nsecret")]
    [InlineData("opaque\r\nX-Injected: yes")]
    public void Constructor_MultilineValue_ShouldRejectAtIssuance(string value)
    {
        // Arrange & Act
        Action create = () => _ = new ApplicationCredential("Bearer", value, DateTimeOffset.UnixEpoch);

        // Assert
        Should.Throw<ArgumentException>(create).Message.ShouldNotContain(value, Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Credential with expressions validate the scheme")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Bad Scheme")]
    [InlineData("Bearer:")]
    public void With_InvalidScheme_ShouldRejectReplacement(string scheme)
    {
        // Arrange
        var credential = new ApplicationCredential("Bearer", "opaque-secret", DateTimeOffset.UnixEpoch);

        // Act
        Action replace = () => _ = credential with { Scheme = scheme };

        // Assert
        Should.Throw<ArgumentException>(replace);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Credential with expressions validate the value")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("opaque\rsecret")]
    [InlineData("opaque\nsecret")]
    public void With_InvalidValue_ShouldRejectReplacement(string value)
    {
        // Arrange
        var credential = new ApplicationCredential("Bearer", "opaque-secret", DateTimeOffset.UnixEpoch);

        // Act
        Action replace = () => _ = credential with { Value = value };

        // Assert
        Should.Throw<ArgumentException>(replace);
    }
}
