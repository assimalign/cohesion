using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityClientPermissionNameTests
{
    [Theory(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept a scope token")]
    [InlineData("openid")]
    [InlineData("orders.read")]
    [InlineData("Orders.Read.All")]
    [InlineData("https://orders.example/read")]
    [InlineData("orders:read")]
    [InlineData("!#$%&'()*+,-./:;<=>?@[]^_`{|}~")]
    public void IsValid_WithAScopeToken_ShouldReturnTrue(string value)
    {
        // Act
        bool valid = IdentityClientPermissionName.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeTrue();
        message.ShouldBeNull();
        new IdentityClientPermissionName(value).Value.ShouldBe(value);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should refuse what RFC 6749 keeps out of a scope token")]
    [InlineData("")]
    [InlineData("orders read")]
    [InlineData("orders\"read")]
    [InlineData("orders\\read")]
    [InlineData("orders\tread")]
    [InlineData("órdenes.read")]
    [InlineData("orders.read\u007F")]
    public void IsValid_WithAValueOutsideTheGrammar_ShouldReturnFalseWithAReason(string value)
    {
        // Act
        bool valid = IdentityClientPermissionName.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeFalse();
        message.ShouldNotBeNullOrWhiteSpace();
        Should.Throw<ArgumentException>(() => new IdentityClientPermissionName(value));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept the longest permission name and refuse one longer")]
    public void IsValid_AtTheLengthLimit_ShouldAcceptTheLimitAndRefuseBeyondIt()
    {
        // Arrange
        string longest = new('s', IdentityClientPermissionName.MaxLength);
        string tooLong = new('s', IdentityClientPermissionName.MaxLength + 1);

        // Act
        bool atLimit = IdentityClientPermissionName.IsValid(longest, out _);
        bool beyond = IdentityClientPermissionName.IsValid(tooLong, out _);

        // Assert
        IdentityClientPermissionName.MaxLength.ShouldBe(128);
        atLimit.ShouldBeTrue();
        beyond.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Equals: Should compare permission names ordinally")]
    public void Equals_WithDifferentCase_ShouldNotBeEqual()
    {
        // Arrange
        IdentityClientPermissionName lower = "orders.read";
        IdentityClientPermissionName mixed = "Orders.Read";

        // Act
        bool equal = lower == mixed;

        // Assert
        equal.ShouldBeFalse();
    }
}
