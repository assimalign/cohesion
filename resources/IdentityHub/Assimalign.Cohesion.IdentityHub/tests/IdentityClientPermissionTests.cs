using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityClientPermissionTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Delegated: Should name a scope of an API")]
    public void Delegated_WithAResourceAndAScope_ShouldCreateADelegatedPermission()
    {
        // Act
        IdentityClientPermission permission = IdentityClientPermission.Delegated(IdentityClientId.FromName("orders-api"), "orders.read");

        // Assert
        permission.Resource.ShouldBe(IdentityClientId.FromName("orders-api"));
        permission.Name.Value.ShouldBe("orders.read");
        permission.Kind.ShouldBe(IdentityClientPermissionKind.Delegated);
        permission.ToString().ShouldBe("bec0a3ee-3f3c-599e-bd0b-3b7194e035ba/orders.read (Delegated)");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Application: Should name a role of an API")]
    public void Application_WithAResourceAndARole_ShouldCreateAnApplicationPermission()
    {
        // Act
        IdentityClientPermission permission = IdentityClientPermission.Application(IdentityClientId.FromName("orders-api"), "orders.process");

        // Assert
        permission.Kind.ShouldBe(IdentityClientPermissionKind.Application);
        permission.ToString().ShouldBe("bec0a3ee-3f3c-599e-bd0b-3b7194e035ba/orders.process (Application)");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should refuse a default resource, a default name, and an undefined kind")]
    public void Constructor_WithAMissingPart_ShouldThrow()
    {
        // Act
        ArgumentException noResource = Should.Throw<ArgumentException>(
            () => IdentityClientPermission.Delegated(default, "orders.read"));
        ArgumentException noName = Should.Throw<ArgumentException>(
            () => IdentityClientPermission.Delegated(IdentityClientId.FromName("orders-api"), default));
        ArgumentOutOfRangeException noKind = Should.Throw<ArgumentOutOfRangeException>(
            () => new IdentityClientPermission(IdentityClientId.FromName("orders-api"), "orders.read", IdentityClientPermissionKind.None));
        ArgumentOutOfRangeException undefinedKind = Should.Throw<ArgumentOutOfRangeException>(
            () => new IdentityClientPermission(IdentityClientId.FromName("orders-api"), "orders.read", (IdentityClientPermissionKind)9));

        // Assert
        noResource.ParamName.ShouldBe("resource");
        noName.ParamName.ShouldBe("name");
        noKind.ParamName.ShouldBe("kind");
        undefinedKind.ParamName.ShouldBe("kind");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Equals: Should tell a delegated permission from an application one of the same name")]
    public void Equals_WithTheSameNameAndAnotherKind_ShouldNotBeEqual()
    {
        // Arrange
        IdentityClientPermission delegated = IdentityClientPermission.Delegated(IdentityClientId.FromName("orders-api"), "orders.read");

        // Act
        bool same = delegated == IdentityClientPermission.Delegated(IdentityClientId.FromName("orders-api"), "orders.read");
        bool otherKind = delegated == IdentityClientPermission.Application(IdentityClientId.FromName("orders-api"), "orders.read");
        bool otherResource = delegated == IdentityClientPermission.Delegated(IdentityClientId.FromName("billing-api"), "orders.read");

        // Assert
        same.ShouldBeTrue();
        otherKind.ShouldBeFalse();
        otherResource.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Default: Should hold nothing and print as empty")]
    public void Default_ShouldHoldNothing()
    {
        // Act
        IdentityClientPermission permission = default;

        // Assert
        permission.Resource.ShouldBe(IdentityClientId.Empty);
        permission.Kind.ShouldBe(IdentityClientPermissionKind.None);
        permission.ToString().ShouldBeEmpty();
    }
}
