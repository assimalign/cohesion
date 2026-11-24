using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityGroupNameTests
{
    [Theory(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept a group name")]
    [InlineData("a")]
    [InlineData("7")]
    [InlineData("orders-admins")]
    [InlineData("Billing.Readers_v2~beta")]
    public void IsValid_WithAGroupName_ShouldReturnTrue(string value)
    {
        // Act
        bool valid = IdentityGroupName.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeTrue();
        message.ShouldBeNull();
        new IdentityGroupName(value).Value.ShouldBe(value);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should refuse what needs escaping in a URL or a token claim list")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("orders admins")]
    [InlineData("orders:admins")]
    [InlineData("orders/admins")]
    [InlineData("orders@contoso")]
    [InlineData("orders+admins")]
    [InlineData("órdenes")]
    [InlineData("orders\nadmins")]
    [InlineData("-orders")]
    [InlineData("orders-")]
    public void IsValid_WithAValueOutsideTheGrammar_ShouldReturnFalseWithAReason(string value)
    {
        // Act
        bool valid = IdentityGroupName.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeFalse();
        message.ShouldNotBeNullOrWhiteSpace();
        Should.Throw<ArgumentException>(() => new IdentityGroupName(value));
        IdentityGroupName.TryCreate(value, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept the longest group name and refuse one longer")]
    public void IsValid_AtTheLengthLimit_ShouldAcceptTheLimitAndRefuseBeyondIt()
    {
        // Arrange
        string longest = new('a', IdentityGroupName.MaxLength);
        string tooLong = new('a', IdentityGroupName.MaxLength + 1);

        // Act
        bool atLimit = IdentityGroupName.IsValid(longest, out _);
        bool beyond = IdentityGroupName.IsValid(tooLong, out _);

        // Assert
        IdentityGroupName.MaxLength.ShouldBe(128);
        atLimit.ShouldBeTrue();
        beyond.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Equals: Should compare group names without regard to case")]
    public void Equals_WithDifferentCase_ShouldBeEqual()
    {
        // Arrange
        IdentityGroupName lower = "orders-admins";
        IdentityGroupName upper = "Orders-ADMINS";
        IdentityGroupName other = "orders-readers";

        // Act
        bool same = lower.Equals(upper);
        bool different = lower.Equals(other);

        // Assert
        same.ShouldBeTrue();
        lower.GetHashCode().ShouldBe(upper.GetHashCode());
        different.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Default: Should hold no group name")]
    public void Default_ShouldHoldNoValue()
    {
        // Act
        IdentityGroupName name = default;

        // Assert
        name.Value.ShouldBeNull();
    }
}
