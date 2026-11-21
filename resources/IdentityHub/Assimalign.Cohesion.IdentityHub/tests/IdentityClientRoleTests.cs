using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityClientRoleTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should keep the name, who may hold the role, and what a person is shown")]
    public void Create_WithEveryPart_ShouldKeepEach()
    {
        // Act
        IdentityClientRole role = IdentityClientRole.Create(
            "orders.process",
            IdentityClientRoleMembers.Users | IdentityClientRoleMembers.Applications,
            "Process orders",
            "Move an order through fulfilment.");

        // Assert
        role.Name.Value.ShouldBe("orders.process");
        role.Members.ShouldBe(IdentityClientRoleMembers.Users | IdentityClientRoleMembers.Applications);
        role.DisplayName.ShouldBe("Process orders");
        role.Description.ShouldBe("Move an order through fulfilment.");
        role.ToString().ShouldBe("orders.process");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should show the name when no display name is given")]
    public void Create_WithNoDisplayName_ShouldShowTheName()
    {
        // Act
        IdentityClientRole role = IdentityClientRole.Create("orders.process", IdentityClientRoleMembers.Applications);

        // Assert
        role.DisplayName.ShouldBe("orders.process");
        role.Description.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a role nobody may hold and an undefined member kind")]
    [InlineData(IdentityClientRoleMembers.None)]
    [InlineData((IdentityClientRoleMembers)4)]
    [InlineData((IdentityClientRoleMembers)7)]
    public void Create_WithMembersOutsideTheDefinedKinds_ShouldThrowArgumentOutOfRangeException(IdentityClientRoleMembers members)
    {
        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(
            () => IdentityClientRole.Create("orders.process", members));

        // Assert
        exception.ParamName.ShouldBe("members");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a default name, a blank display name, and a description beyond its limit")]
    public void Create_WithAPartOutsideItsRule_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException noName = Should.Throw<ArgumentException>(
            () => IdentityClientRole.Create(default, IdentityClientRoleMembers.Users));
        ArgumentException blank = Should.Throw<ArgumentException>(
            () => IdentityClientRole.Create("orders.process", IdentityClientRoleMembers.Users, " "));
        ArgumentException longDescription = Should.Throw<ArgumentException>(
            () => IdentityClientRole.Create(
                "orders.process",
                IdentityClientRoleMembers.Users,
                description: new string('d', IdentityClient.MaxDescriptionLength + 1)));

        // Assert
        noName.ParamName.ShouldBe("name");
        blank.ParamName.ShouldBe("displayName");
        longDescription.ParamName.ShouldBe("description");
    }
}
