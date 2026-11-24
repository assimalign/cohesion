using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityGroupIdTests
{
    // The expected identifiers were computed outside .NET, with Python's uuid.uuid5 over the
    // group identifier namespace, so the test pins the contract and not this implementation.
    [Theory(DisplayName = "Cohesion Test [IdentityHub] - FromName: Should derive the version 5 UUID of the name in the group identifier namespace")]
    [InlineData("orders-admins", "0933ab2c-248b-50ff-919a-577b7ead6f89")]
    [InlineData("billing.readers", "513efcf9-6e96-5ee2-b48a-c8827c22c72d")]
    [InlineData("everyone", "f3480492-e057-579c-b2e2-d75ba4bdd991")]
    public void FromName_WithAName_ShouldDeriveThePinnedIdentifier(string name, string expected)
    {
        // Act
        IdentityGroupId id = IdentityGroupId.FromName(name);
        IdentityGroupId again = IdentityGroupId.FromName(name);

        // Assert
        id.ToString().ShouldBe(expected);
        id.Value.Version.ShouldBe(5);
        again.ShouldBe(id);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - FromName: Should derive one identifier from group names that differ only by case")]
    [InlineData("Orders-Admins")]
    [InlineData("ORDERS-ADMINS")]
    public void FromName_WithAnotherCaseOfTheName_ShouldDeriveTheSameIdentifier(string name)
    {
        // Act
        IdentityGroupId id = IdentityGroupId.FromName(name);

        // Assert
        id.ShouldBe(IdentityGroupId.FromName("orders-admins"));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FromName: Should refuse a default group name")]
    public void FromName_WithADefaultName_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => IdentityGroupId.FromName(default));

        // Assert
        exception.ParamName.ShouldBe("name");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - New: Should return a random group identifier that is not empty")]
    public void New_ShouldReturnDistinctIdentifiers()
    {
        // Act
        IdentityGroupId first = IdentityGroupId.New();
        IdentityGroupId second = IdentityGroupId.New();

        // Assert
        first.ShouldNotBe(IdentityGroupId.Empty);
        first.ShouldNotBe(second);
    }
}
