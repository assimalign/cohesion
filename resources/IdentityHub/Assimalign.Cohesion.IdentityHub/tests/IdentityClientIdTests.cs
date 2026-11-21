using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityClientIdTests
{
    // The expected identifiers were computed outside .NET, with Python's uuid.uuid5 over the
    // client identifier namespace, so the test pins the contract and not this implementation.
    [Theory(DisplayName = "Cohesion Test [IdentityHub] - FromName: Should derive the version 5 UUID of the name in the client identifier namespace")]
    [InlineData("orders-api", "bec0a3ee-3f3c-599e-bd0b-3b7194e035ba")]
    [InlineData("orders-web", "c9beff70-d768-58c4-8767-d9ffea943d0a")]
    [InlineData("billing.worker", "a649939d-3068-555e-a9af-72ef18a5fdb8")]
    public void FromName_WithAName_ShouldDeriveThePinnedIdentifier(string name, string expected)
    {
        // Act
        IdentityClientId id = IdentityClientId.FromName(name);
        IdentityClientId again = IdentityClientId.FromName(name);

        // Assert
        id.ToString().ShouldBe(expected);
        id.Value.Version.ShouldBe(5);
        again.ShouldBe(id);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - FromName: Should derive one identifier from names that differ only by case")]
    [InlineData("Orders-API")]
    [InlineData("ORDERS-API")]
    [InlineData("orders-Api")]
    public void FromName_WithAnotherCaseOfTheName_ShouldDeriveTheSameIdentifier(string name)
    {
        // Act
        IdentityClientId id = IdentityClientId.FromName(name);

        // Assert
        id.ShouldBe(IdentityClientId.FromName("orders-api"));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FromName: Should derive different identifiers from different names")]
    public void FromName_WithDifferentNames_ShouldDeriveDifferentIdentifiers()
    {
        // Act
        IdentityClientId api = IdentityClientId.FromName("orders-api");
        IdentityClientId web = IdentityClientId.FromName("orders-web");
        IdentityClientId nearby = IdentityClientId.FromName("orders_api");

        // Assert
        api.ShouldNotBe(web);
        api.ShouldNotBe(nearby);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FromName: Should refuse a default name")]
    public void FromName_WithADefaultName_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => IdentityClientId.FromName(default));

        // Assert
        exception.ParamName.ShouldBe("name");
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - FromName: Should refuse text that is not a client name before it derives anything")]
    [InlineData("")]
    [InlineData("orders api")]
    [InlineData("orders/api")]
    [InlineData("-orders")]
    public void FromName_WithTextOutsideTheNameGrammar_ShouldThrowArgumentException(string name)
    {
        // Act and Assert
        Should.Throw<ArgumentException>(() => IdentityClientId.FromName(name));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - New: Should return a random identifier that is not empty")]
    public void New_ShouldReturnDistinctIdentifiers()
    {
        // Act
        IdentityClientId first = IdentityClientId.New();
        IdentityClientId second = IdentityClientId.New();

        // Assert
        first.ShouldNotBe(IdentityClientId.Empty);
        first.ShouldNotBe(second);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - ToString: Should print the lowercase form with hyphens and parse it back")]
    public void ToString_ShouldPrintTheCanonicalTextAndRoundTrip()
    {
        // Arrange
        var id = new IdentityClientId(Guid.Parse("BEC0A3EE-3F3C-599E-BD0B-3B7194E035BA"));

        // Act
        string text = id.ToString();
        IdentityClientId parsed = IdentityClientId.Parse(text);

        // Assert
        text.ShouldBe("bec0a3ee-3f3c-599e-bd0b-3b7194e035ba");
        parsed.ShouldBe(id);
        parsed.ShouldBe(IdentityClientId.FromName("orders-api"));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Default: Should be the empty identifier")]
    public void Default_ShouldBeTheEmptyIdentifier()
    {
        // Act
        IdentityClientId id = default;

        // Assert
        id.ShouldBe(IdentityClientId.Empty);
        id.Value.ShouldBe(Guid.Empty);
    }
}
