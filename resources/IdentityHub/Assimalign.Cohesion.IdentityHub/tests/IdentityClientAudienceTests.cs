using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityClientAudienceTests
{
    [Theory(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept a string or a URI as an audience")]
    [InlineData("orders")]
    [InlineData("orders-api")]
    [InlineData("api://orders")]
    [InlineData("https://orders.example/api")]
    [InlineData("urn:cohesion:orders")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("/orders")]
    public void IsValid_WithAnAudience_ShouldReturnTrue(string value)
    {
        // Act
        bool valid = IdentityClientAudience.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeTrue();
        message.ShouldBeNull();
        new IdentityClientAudience(value).Value.ShouldBe(value);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should refuse an audience that is neither a plain string nor a URI")]
    [InlineData("")]
    [InlineData("orders api")]
    [InlineData("órdenes")]
    [InlineData("orders\n")]
    [InlineData(":orders")]
    [InlineData("1api://orders")]
    [InlineData("api_v2://orders")]
    [InlineData("api://orders#section")]
    public void IsValid_WithAValueOutsideTheGrammar_ShouldReturnFalseWithAReason(string value)
    {
        // Act
        bool valid = IdentityClientAudience.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeFalse();
        message.ShouldNotBeNullOrWhiteSpace();
        Should.Throw<ArgumentException>(() => new IdentityClientAudience(value));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept the longest audience and refuse one longer")]
    public void IsValid_AtTheLengthLimit_ShouldAcceptTheLimitAndRefuseBeyondIt()
    {
        // Arrange
        string longest = new('a', IdentityClientAudience.MaxLength);
        string tooLong = new('a', IdentityClientAudience.MaxLength + 1);

        // Act
        bool atLimit = IdentityClientAudience.IsValid(longest, out _);
        bool beyond = IdentityClientAudience.IsValid(tooLong, out _);

        // Assert
        IdentityClientAudience.MaxLength.ShouldBe(256);
        atLimit.ShouldBeTrue();
        beyond.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Equals: Should compare audiences ordinally, as RFC 7519 does")]
    public void Equals_WithDifferentCase_ShouldNotBeEqual()
    {
        // Arrange
        IdentityClientAudience lower = "api://orders";
        IdentityClientAudience mixed = "api://Orders";

        // Act
        bool equal = lower == mixed;

        // Assert
        equal.ShouldBeFalse();
    }
}
