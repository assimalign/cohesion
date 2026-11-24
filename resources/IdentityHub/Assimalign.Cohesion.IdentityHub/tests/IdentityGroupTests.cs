using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// A group is created whole from a descriptor, or not at all.
/// </summary>
public class IdentityGroupTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a null group descriptor")]
    public void Create_WithANullDescriptor_ShouldThrowArgumentNullException()
    {
        // Act
        ArgumentNullException exception = Should.Throw<ArgumentNullException>(() => IdentityGroup.Create(null!));

        // Assert
        exception.ParamName.ShouldBe("descriptor");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should derive the identifier and the display name from the group's name")]
    public void Create_WithOnlyAName_ShouldDeriveTheRest()
    {
        // Act
        IdentityGroup group = IdentityGroup.Create(new() { Name = "Orders-Admins" });

        // Assert
        group.Id.ShouldBe(IdentityGroupId.FromName("orders-admins"));
        group.Name.Value.ShouldBe("Orders-Admins");
        group.DisplayName.ShouldBe("Orders-Admins");
        group.Description.ShouldBeNull();
        group.IsLocked.ShouldBeFalse();
        group.Version.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should keep everything a group descriptor gives")]
    public void Create_WithAFullDescriptor_ShouldKeepEveryMember()
    {
        // Arrange
        IdentityGroupId id = IdentityGroupId.New();

        // Act
        IdentityGroup group = IdentityGroup.Create(new()
        {
            Id = id,
            Name = "orders-admins",
            DisplayName = "Orders administrators",
            Description = "Everyone who may change an order.\nReviewed quarterly.",
        });

        // Assert
        group.Id.ShouldBe(id);
        group.Name.Value.ShouldBe("orders-admins");
        group.DisplayName.ShouldBe("Orders administrators");
        group.Description.ShouldBe("Everyone who may change an order.\nReviewed quarterly.");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a group with no name")]
    public void Create_WithNoName_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => IdentityGroup.Create(new() { Id = IdentityGroupId.New() }));

        // Assert
        exception.ParamName.ShouldBe("descriptor");
        exception.Message.ShouldContain("group name is required", Case.Insensitive);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a display name or a description that is blank, holds a control character, or is too long")]
    [InlineData("display", "   ")]
    [InlineData("display", "Orders\nadministrators")]
    [InlineData("description", "   ")]
    [InlineData("description", "Orders\u0000")]
    public void Create_WithInvalidText_ShouldThrowArgumentExceptionNamingTheGroup(string member, string value)
    {
        // Arrange
        var descriptor = new IdentityGroupDescriptor { Name = "orders-admins" };
        if (member is "display")
        {
            descriptor.DisplayName = value;
        }
        else
        {
            descriptor.Description = value;
        }

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => IdentityGroup.Create(descriptor));

        // Assert
        exception.ParamName.ShouldBe("descriptor");
        exception.Message.ShouldContain($"'orders-admins ({IdentityGroupId.FromName("orders-admins")})'", Case.Sensitive);
        exception.Message.ShouldContain(member, Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should accept group text at its limit and refuse it beyond")]
    public void Create_AtTheTextLimits_ShouldAcceptTheLimitAndRefuseBeyondIt()
    {
        // Arrange
        string displayName = new('d', IdentityGroup.MaxDisplayNameLength);
        string description = new('e', IdentityGroup.MaxDescriptionLength);

        // Act
        IdentityGroup atLimit = IdentityGroup.Create(new()
        {
            Name = "orders-admins",
            DisplayName = displayName,
            Description = description,
        });

        // Assert
        IdentityGroup.MaxDisplayNameLength.ShouldBe(128);
        IdentityGroup.MaxDescriptionLength.ShouldBe(256);
        atLimit.Description!.Length.ShouldBe(256);
        Should.Throw<ArgumentException>(
            () => IdentityGroup.Create(new() { Name = "orders-admins", DisplayName = displayName + "d" }));
        Should.Throw<ArgumentException>(
            () => IdentityGroup.Create(new() { Name = "orders-admins", Description = description + "e" }));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - From: Should describe a group so that it creates an equal one")]
    public void From_WithAGroup_ShouldRoundTripEveryMember()
    {
        // Arrange
        IdentityGroup group = IdentityGroup.Create(new()
        {
            Id = IdentityGroupId.New(),
            Name = "orders-admins",
            DisplayName = "Orders administrators",
            Description = "Everyone who may change an order.",
        });

        // Act
        IdentityGroup copy = IdentityGroup.Create(IdentityGroupDescriptor.From(group));

        // Assert
        copy.Id.ShouldBe(group.Id);
        copy.Name.ShouldBe(group.Name);
        copy.DisplayName.ShouldBe(group.DisplayName);
        copy.Description.ShouldBe(group.Description);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - From: Should keep a group's identifier when its name changes")]
    public void From_WhenTheNameChanges_ShouldKeepTheIdentifier()
    {
        // Arrange
        IdentityGroup group = IdentityGroup.Create(new() { Name = "orders-admins" });
        IdentityGroupDescriptor descriptor = IdentityGroupDescriptor.From(group);

        // Act
        descriptor.Name = "orders-owners";
        IdentityGroup renamed = IdentityGroup.Create(descriptor);

        // Assert
        renamed.Id.ShouldBe(IdentityGroupId.FromName("orders-admins"));
        renamed.Name.Value.ShouldBe("orders-owners");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - From: Should refuse a null group")]
    public void From_WithANullGroup_ShouldThrowArgumentNullException()
    {
        // Act
        ArgumentNullException exception = Should.Throw<ArgumentNullException>(() => IdentityGroupDescriptor.From(null!));

        // Assert
        exception.ParamName.ShouldBe("group");
    }
}
