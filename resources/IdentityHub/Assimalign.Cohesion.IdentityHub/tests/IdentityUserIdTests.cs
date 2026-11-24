using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityUserIdTests
{
    // The expected identifiers were computed outside .NET, with Python's uuid.uuid5 over the
    // user identifier namespace, so the test pins the contract and not this implementation.
    [Theory(DisplayName = "Cohesion Test [IdentityHub] - FromName: Should derive the version 5 UUID of the name in the user identifier namespace")]
    [InlineData("ada", "e2bffadd-19cb-5bce-817b-8a6008fd4d28")]
    [InlineData("ada@contoso.example", "47dc2387-b6b6-5006-b279-d4d23896d554")]
    [InlineData("grace.hopper+ops@contoso.example", "d3c75f61-d163-5f68-ad5c-f30854bc1584")]
    public void FromName_WithAName_ShouldDeriveThePinnedIdentifier(string name, string expected)
    {
        // Act
        IdentityUserId id = IdentityUserId.FromName(name);
        IdentityUserId again = IdentityUserId.FromName(name);

        // Assert
        id.ToString().ShouldBe(expected);
        id.Value.Version.ShouldBe(5);
        again.ShouldBe(id);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - FromName: Should derive one identifier from user names that differ only by case")]
    [InlineData("Ada@Contoso.example")]
    [InlineData("ADA@CONTOSO.EXAMPLE")]
    public void FromName_WithAnotherCaseOfTheName_ShouldDeriveTheSameIdentifier(string name)
    {
        // Act
        IdentityUserId id = IdentityUserId.FromName(name);

        // Assert
        id.ShouldBe(IdentityUserId.FromName("ada@contoso.example"));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FromName: Should not derive a group's or a client's identifier from the same name")]
    public void FromName_WithANameAnotherKindUses_ShouldDeriveADifferentIdentifier()
    {
        // Act
        Guid user = IdentityUserId.FromName("orders").Value;
        Guid group = IdentityGroupId.FromName("orders").Value;
        Guid client = IdentityClientId.FromName("orders").Value;

        // Assert
        user.ShouldNotBe(group);
        user.ShouldNotBe(client);
        group.ShouldNotBe(client);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FromName: Should refuse a default user name")]
    public void FromName_WithADefaultName_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => IdentityUserId.FromName(default));

        // Assert
        exception.ParamName.ShouldBe("name");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - New: Should return a random user identifier that is not empty")]
    public void New_ShouldReturnDistinctIdentifiers()
    {
        // Act
        IdentityUserId first = IdentityUserId.New();
        IdentityUserId second = IdentityUserId.New();

        // Assert
        first.ShouldNotBe(IdentityUserId.Empty);
        first.ShouldNotBe(second);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - ToString: Should print a user identifier in lowercase with hyphens and parse it back")]
    public void ToString_ShouldPrintTheCanonicalTextAndRoundTrip()
    {
        // Arrange
        var id = new IdentityUserId(Guid.Parse("E2BFFADD-19CB-5BCE-817B-8A6008FD4D28"));

        // Act
        string text = id.ToString();
        IdentityUserId parsed = IdentityUserId.Parse(text);

        // Assert
        text.ShouldBe("e2bffadd-19cb-5bce-817b-8a6008fd4d28");
        parsed.ShouldBe(id);
        parsed.ShouldBe(IdentityUserId.FromName("ada"));
    }
}
