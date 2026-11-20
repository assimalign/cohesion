using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityHubRecordSetNameTests
{
    [Theory(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept a record set name")]
    [InlineData("a")]
    [InlineData("clients")]
    [InlineData("device-authorizations")]
    [InlineData("identityhub.signing-keys")]
    [InlineData("v2.grants")]
    [InlineData("a1.b2-c3")]
    public void IsValid_WithARecordSetName_ShouldReturnTrue(string value)
    {
        // Act
        bool valid = IdentityHubRecordSetName.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeTrue();
        message.ShouldBeNull();
        new IdentityHubRecordSetName(value).Value.ShouldBe(value);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should refuse a value that is not a record set name")]
    [InlineData("")]
    [InlineData("Clients")]
    [InlineData("cLients")]
    [InlineData("1clients")]
    [InlineData("-clients")]
    [InlineData(".clients")]
    [InlineData("clients-")]
    [InlineData("clients.")]
    [InlineData("device..authorizations")]
    [InlineData("device.-authorizations")]
    [InlineData("device--authorizations")]
    [InlineData("device_authorizations")]
    [InlineData("device authorizations")]
    [InlineData("clients/keys")]
    [InlineData("clés")]
    [InlineData("clients\n")]
    public void IsValid_WithAValueOutsideTheGrammar_ShouldReturnFalseWithAReason(string value)
    {
        // Act
        bool valid = IdentityHubRecordSetName.IsValid(value, out string? message);

        // Assert
        valid.ShouldBeFalse();
        message.ShouldNotBeNullOrWhiteSpace();
        Should.Throw<ArgumentException>(() => new IdentityHubRecordSetName(value));
        IdentityHubRecordSetName.TryCreate(value, out IdentityHubRecordSetName name).ShouldBeFalse();
        name.Value.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - IsValid: Should accept the longest name and refuse one character more")]
    public void IsValid_AtTheLengthLimit_ShouldAcceptTheLimitAndRefuseBeyondIt()
    {
        // Arrange
        string longest = new('a', IdentityHubRecordSetName.MaxLength);
        string tooLong = new('a', IdentityHubRecordSetName.MaxLength + 1);

        // Act & Assert
        IdentityHubRecordSetName.MaxLength.ShouldBe(64);
        IdentityHubRecordSetName.IsValid(longest, out _).ShouldBeTrue();
        IdentityHubRecordSetName.IsValid(tooLong, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryCreate: Should refuse null and create from a valid name")]
    public void TryCreate_WithNullOrAValidName_ShouldReportWhetherItCreated()
    {
        // Act
        bool fromNull = IdentityHubRecordSetName.TryCreate(null, out IdentityHubRecordSetName none);
        bool fromName = IdentityHubRecordSetName.TryCreate("clients", out IdentityHubRecordSetName name);

        // Assert
        fromNull.ShouldBeFalse();
        none.Value.ShouldBeNull();
        fromName.ShouldBeTrue();
        name.Value.ShouldBe("clients");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Equals: Should compare names ordinally")]
    public void Equals_WithAnotherName_ShouldCompareOrdinally()
    {
        // Arrange
        IdentityHubRecordSetName name = "clients";

        // Act & Assert
        (name == new IdentityHubRecordSetName("clients")).ShouldBeTrue();
        (name != new IdentityHubRecordSetName("client")).ShouldBeTrue();
        name.GetHashCode().ShouldBe(new IdentityHubRecordSetName("clients").GetHashCode());
        name.ToString().ShouldBe("clients");
        string text = name;
        text.ShouldBe("clients");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Default: Should hold no name")]
    public void Default_OfAName_ShouldHoldNoValue()
    {
        // Act
        IdentityHubRecordSetName name = default;

        // Assert
        name.Value.ShouldBeNull();
        name.ToString().ShouldBe(string.Empty);
        (name == new IdentityHubRecordSetName("clients")).ShouldBeFalse();
    }
}
