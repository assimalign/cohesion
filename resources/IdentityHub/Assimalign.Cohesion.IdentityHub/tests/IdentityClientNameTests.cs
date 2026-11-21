using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityClientNameTests
{
    [Theory(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept a client name")]
    [InlineData("a")]
    [InlineData("7")]
    [InlineData("orders-api")]
    [InlineData("Orders.Api_v2~beta")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    public void IsValid_WithAClientName_ShouldReturnTrue(string value)
    {
        // Act
        bool valid = IdentityClientName.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeTrue();
        message.ShouldBeNull();
        new IdentityClientName(value).Value.ShouldBe(value);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should refuse what needs escaping in a URL, a form, or Basic authentication")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("orders api")]
    [InlineData("orders:api")]
    [InlineData("orders/api")]
    [InlineData("orders@example")]
    [InlineData("orders+api")]
    [InlineData("órdenes")]
    [InlineData("orders\napi")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("-orders")]
    [InlineData("orders-")]
    [InlineData(".orders")]
    [InlineData("orders~")]
    public void IsValid_WithAValueOutsideTheGrammar_ShouldReturnFalseWithAReason(string value)
    {
        // Act
        bool valid = IdentityClientName.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeFalse();
        message.ShouldNotBeNullOrWhiteSpace();
        Should.Throw<ArgumentException>(() => new IdentityClientName(value));
        IdentityClientName.TryCreate(value, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept the longest client name and refuse one longer")]
    public void IsValid_AtTheLengthLimit_ShouldAcceptTheLimitAndRefuseBeyondIt()
    {
        // Arrange
        string longest = new('a', IdentityClientName.MaxLength);
        string tooLong = new('a', IdentityClientName.MaxLength + 1);

        // Act
        bool atLimit = IdentityClientName.IsValid(longest, out _);
        bool beyond = IdentityClientName.IsValid(tooLong, out _);

        // Assert
        IdentityClientName.MaxLength.ShouldBe(128);
        atLimit.ShouldBeTrue();
        beyond.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Equals: Should compare client names without regard to case")]
    public void Equals_WithDifferentCase_ShouldBeEqual()
    {
        // Arrange
        IdentityClientName lower = "orders-api";
        IdentityClientName upper = "Orders-API";
        IdentityClientName other = "orders-web";

        // Act
        bool equal = lower == upper;
        bool sameHash = lower.GetHashCode() == upper.GetHashCode();
        bool different = lower == other;

        // Assert
        equal.ShouldBeTrue();
        sameHash.ShouldBeTrue();
        different.ShouldBeFalse();
        upper.Value.ShouldBe("Orders-API");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Default: Should hold no value")]
    public void Default_ShouldHoldNoValue()
    {
        // Act
        IdentityClientName name = default;

        // Assert
        name.Value.ShouldBeNull();
        name.ToString().ShouldBeEmpty();
    }
}
