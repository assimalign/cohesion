using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityUserNameTests
{
    [Theory(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept a user name")]
    [InlineData("a")]
    [InlineData("7")]
    [InlineData("ada")]
    [InlineData("ada.lovelace")]
    [InlineData("ada@contoso.example")]
    [InlineData("Ada.Lovelace+ops@Contoso.example")]
    [InlineData("svc_orders~2")]
    public void IsValid_WithAUserName_ShouldReturnTrue(string value)
    {
        // Act
        bool valid = IdentityUserName.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeTrue();
        message.ShouldBeNull();
        new IdentityUserName(value).Value.ShouldBe(value);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should refuse what needs escaping in a form or a query, and a second at sign")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("ada lovelace")]
    [InlineData("ada:lovelace")]
    [InlineData("ada/lovelace")]
    [InlineData("ada@contoso@example")]
    [InlineData("@contoso.example")]
    [InlineData("ada@")]
    [InlineData("ada\nlovelace")]
    [InlineData("adá")]
    [InlineData(".ada")]
    [InlineData("ada.")]
    [InlineData("+ada")]
    [InlineData("ada%40contoso.example")]
    public void IsValid_WithAValueOutsideTheGrammar_ShouldReturnFalseWithAReason(string value)
    {
        // Act
        bool valid = IdentityUserName.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeFalse();
        message.ShouldNotBeNullOrWhiteSpace();
        Should.Throw<ArgumentException>(() => new IdentityUserName(value));
        IdentityUserName.TryCreate(value, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should not echo the refused user name")]
    public void IsValid_WithARefusedValue_ShouldNotEchoIt()
    {
        // Act
        IdentityUserName.IsValid("ada lovelace<script>", out string? message);

        // Assert
        message.ShouldNotBeNull();
        message.ShouldNotContain("script");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept the longest user name and refuse one longer")]
    public void IsValid_AtTheLengthLimit_ShouldAcceptTheLimitAndRefuseBeyondIt()
    {
        // Arrange
        string longest = new('a', IdentityUserName.MaxLength);
        string tooLong = new('a', IdentityUserName.MaxLength + 1);

        // Act
        bool atLimit = IdentityUserName.IsValid(longest, out _);
        bool beyond = IdentityUserName.IsValid(tooLong, out _);

        // Assert
        IdentityUserName.MaxLength.ShouldBe(128);
        atLimit.ShouldBeTrue();
        beyond.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Equals: Should compare user names without regard to case")]
    public void Equals_WithDifferentCase_ShouldBeEqual()
    {
        // Arrange
        IdentityUserName lower = "ada@contoso.example";
        IdentityUserName upper = "Ada@CONTOSO.example";
        IdentityUserName other = "grace@contoso.example";

        // Act
        bool same = lower.Equals(upper);
        bool different = lower.Equals(other);

        // Assert
        same.ShouldBeTrue();
        lower.GetHashCode().ShouldBe(upper.GetHashCode());
        different.ShouldBeFalse();
        upper.Value.ShouldBe("Ada@CONTOSO.example");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Default: Should hold no user name")]
    public void Default_ShouldHoldNoValue()
    {
        // Act
        IdentityUserName name = default;

        // Assert
        name.Value.ShouldBeNull();
    }
}
