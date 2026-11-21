using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityHubRecordVersionTests
{
    [Theory(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept a record version")]
    [InlineData("1")]
    [InlineData("18446744073709551615")]
    [InlineData("0x00000000000007D1")]
    [InlineData("W/\"5f3a\"")]
    [InlineData("3q2-7w==")]
    [InlineData("!~")]
    public void IsValid_WithARecordVersion_ShouldReturnTrue(string value)
    {
        // Act
        bool valid = IdentityHubRecordVersion.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeTrue();
        message.ShouldBeNull();
        new IdentityHubRecordVersion(value).Value.ShouldBe(value);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should refuse a value that is not a record version")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1 2")]
    [InlineData("1\t")]
    [InlineData("1\n")]
    [InlineData("version\u007F")]
    [InlineData("versión")]
    public void IsValid_WithAValueOutsideTheGrammar_ShouldReturnFalseWithAReason(string value)
    {
        // Act
        bool valid = IdentityHubRecordVersion.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeFalse();
        message.ShouldNotBeNullOrWhiteSpace();
        Should.Throw<ArgumentException>(() => new IdentityHubRecordVersion(value));
        IdentityHubRecordVersion.TryCreate(value, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept the longest version and refuse one character more")]
    public void IsValid_AtTheLengthLimit_ShouldAcceptTheLimitAndRefuseBeyondIt()
    {
        // Arrange
        string longest = new('9', IdentityHubRecordVersion.MaxLength);
        string tooLong = new('9', IdentityHubRecordVersion.MaxLength + 1);

        // Act & Assert
        IdentityHubRecordVersion.MaxLength.ShouldBe(128);
        IdentityHubRecordVersion.IsValid(longest, out _).ShouldBeTrue();
        IdentityHubRecordVersion.IsValid(tooLong, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Equals: Should compare versions ordinally")]
    public void Equals_WithAnotherVersion_ShouldCompareOrdinally()
    {
        // Arrange
        IdentityHubRecordVersion version = "0xAB";

        // Act & Assert
        (version == new IdentityHubRecordVersion("0xAB")).ShouldBeTrue();
        (version == new IdentityHubRecordVersion("0xab")).ShouldBeFalse();
        version.ToString().ShouldBe("0xAB");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Default: Should hold no version")]
    public void Default_OfAVersion_ShouldHoldNoValue()
    {
        // Act
        IdentityHubRecordVersion version = default;

        // Assert
        version.Value.ShouldBeNull();
        version.ToString().ShouldBe(string.Empty);
        IdentityHubRecordVersion.TryCreate(null, out _).ShouldBeFalse();
    }
}
