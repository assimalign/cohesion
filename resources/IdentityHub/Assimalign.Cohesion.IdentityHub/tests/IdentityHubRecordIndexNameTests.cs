using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityHubRecordIndexNameTests
{
    [Theory(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept a record index name")]
    [InlineData("a")]
    [InlineData("subject")]
    [InlineData("user-code")]
    [InlineData("directory.user-name")]
    [InlineData("email2")]
    public void IsValid_WithARecordIndexName_ShouldReturnTrue(string value)
    {
        // Act
        bool valid = IdentityHubRecordIndexName.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeTrue();
        message.ShouldBeNull();
        new IdentityHubRecordIndexName(value).Value.ShouldBe(value);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should refuse a value that is not a record index name")]
    [InlineData("")]
    [InlineData("Subject")]
    [InlineData("userCode")]
    [InlineData("2fa")]
    [InlineData("-subject")]
    [InlineData("subject-")]
    [InlineData("user--code")]
    [InlineData("user_code")]
    [InlineData("user code")]
    [InlineData("user:code")]
    public void IsValid_WithAValueOutsideTheGrammar_ShouldReturnFalseWithAReason(string value)
    {
        // Act
        bool valid = IdentityHubRecordIndexName.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeFalse();
        message.ShouldNotBeNullOrWhiteSpace();
        Should.Throw<ArgumentException>(() => new IdentityHubRecordIndexName(value));
        IdentityHubRecordIndexName.TryCreate(value, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept the longest name and refuse one character more")]
    public void IsValid_AtTheLengthLimit_ShouldAcceptTheLimitAndRefuseBeyondIt()
    {
        // Arrange
        string longest = new('a', IdentityHubRecordIndexName.MaxLength);
        string tooLong = new('a', IdentityHubRecordIndexName.MaxLength + 1);

        // Act & Assert
        IdentityHubRecordIndexName.MaxLength.ShouldBe(64);
        IdentityHubRecordIndexName.IsValid(longest, out _).ShouldBeTrue();
        IdentityHubRecordIndexName.IsValid(tooLong, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Equals: Should compare names ordinally")]
    public void Equals_WithAnotherName_ShouldCompareOrdinally()
    {
        // Arrange
        IdentityHubRecordIndexName name = "user-code";

        // Act & Assert
        (name == new IdentityHubRecordIndexName("user-code")).ShouldBeTrue();
        (name != new IdentityHubRecordIndexName("user-codes")).ShouldBeTrue();
        name.ToString().ShouldBe("user-code");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Default: Should hold no name")]
    public void Default_OfAName_ShouldHoldNoValue()
    {
        // Act
        IdentityHubRecordIndexName name = default;

        // Assert
        name.Value.ShouldBeNull();
        name.ToString().ShouldBe(string.Empty);
    }
}
