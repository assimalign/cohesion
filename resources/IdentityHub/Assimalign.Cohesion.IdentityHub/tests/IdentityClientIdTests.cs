using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityClientIdTests
{
    [Theory(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept a client identifier")]
    [InlineData("a")]
    [InlineData("7")]
    [InlineData("orders-api")]
    [InlineData("Orders.Api_v2~beta")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    public void IsValid_WithAClientIdentifier_ShouldReturnTrue(string value)
    {
        // Act
        bool valid = IdentityClientId.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeTrue();
        message.ShouldBeNull();
        new IdentityClientId(value).Value.ShouldBe(value);
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
        bool valid = IdentityClientId.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeFalse();
        message.ShouldNotBeNullOrWhiteSpace();
        Should.Throw<ArgumentException>(() => new IdentityClientId(value));
        IdentityClientId.TryCreate(value, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept the longest client identifier and refuse one longer")]
    public void IsValid_AtTheLengthLimit_ShouldAcceptTheLimitAndRefuseBeyondIt()
    {
        // Arrange
        string longest = new('a', IdentityClientId.MaxLength);
        string tooLong = new('a', IdentityClientId.MaxLength + 1);

        // Act
        bool atLimit = IdentityClientId.IsValid(longest, out _);
        bool beyond = IdentityClientId.IsValid(tooLong, out _);

        // Assert
        IdentityClientId.MaxLength.ShouldBe(128);
        atLimit.ShouldBeTrue();
        beyond.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Equals: Should compare client identifiers ordinally")]
    public void Equals_WithDifferentCase_ShouldNotBeEqual()
    {
        // Arrange
        IdentityClientId lower = "orders-api";
        IdentityClientId upper = "Orders-Api";

        // Act
        bool equal = lower == upper;

        // Assert
        equal.ShouldBeFalse();
        (lower == new IdentityClientId("orders-api")).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Default: Should hold no value")]
    public void Default_ShouldHoldNoValue()
    {
        // Act
        IdentityClientId id = default;

        // Assert
        id.Value.ShouldBeNull();
        id.ToString().ShouldBeEmpty();
    }
}
