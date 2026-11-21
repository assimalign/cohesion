using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityClientCredentialIdTests
{
    [Theory(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept a credential identifier")]
    [InlineData("primary")]
    [InlineData("2026-01")]
    [InlineData("build.pipeline_main")]
    public void IsValid_WithACredentialIdentifier_ShouldReturnTrue(string value)
    {
        // Act
        bool valid = IdentityClientCredentialId.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeTrue();
        message.ShouldBeNull();
        new IdentityClientCredentialId(value).Value.ShouldBe(value);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should refuse a credential identifier outside the grammar")]
    [InlineData("")]
    [InlineData("primary key")]
    [InlineData("primary/key")]
    [InlineData("-primary")]
    [InlineData("primary.")]
    public void IsValid_WithAValueOutsideTheGrammar_ShouldReturnFalseWithAReason(string value)
    {
        // Act
        bool valid = IdentityClientCredentialId.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeFalse();
        message.ShouldNotBeNullOrWhiteSpace();
        Should.Throw<ArgumentException>(() => new IdentityClientCredentialId(value));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept the longest credential identifier and refuse one longer")]
    public void IsValid_AtTheLengthLimit_ShouldAcceptTheLimitAndRefuseBeyondIt()
    {
        // Arrange
        string longest = new('k', IdentityClientCredentialId.MaxLength);
        string tooLong = new('k', IdentityClientCredentialId.MaxLength + 1);

        // Act
        bool atLimit = IdentityClientCredentialId.IsValid(longest, out _);
        bool beyond = IdentityClientCredentialId.IsValid(tooLong, out _);

        // Assert
        IdentityClientCredentialId.MaxLength.ShouldBe(64);
        atLimit.ShouldBeTrue();
        beyond.ShouldBeFalse();
    }
}
