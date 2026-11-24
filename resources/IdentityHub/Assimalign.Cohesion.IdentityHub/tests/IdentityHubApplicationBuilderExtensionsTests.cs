using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// The verbs that declare a user or a group from a name or an identifier and a callback: each
/// creates the object, which checks it whole, and hands it to the builder member that takes it.
/// </summary>
public class IdentityHubApplicationBuilderExtensionsTests
{
    private readonly RecordingIdentityHubApplicationBuilder _recording = new();

    private IIdentityHubApplicationBuilder Builder => _recording;

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - AddUser: Should define a user from a name alone, with the identifier derived from it")]
    public void AddUser_WithANameAlone_ShouldDefineTheUserUnderTheDerivedIdentifier()
    {
        // Act
        IIdentityHubApplicationBuilder returned = Builder.AddUser("Ada@Contoso.example");

        // Assert
        returned.ShouldBeSameAs(_recording);
        IdentityUser user = _recording.Users.ShouldHaveSingleItem();
        user.Id.ShouldBe(IdentityUserId.FromName("ada@contoso.example"));
        user.Name.Value.ShouldBe("Ada@Contoso.example");
        user.DisplayName.ShouldBe("Ada@Contoso.example");
        user.IsEnabled.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - AddUser: Should hand the callback a descriptor that already holds the name")]
    public void AddUser_WithANameAndACallback_ShouldFillTheRestOfTheUser()
    {
        // Arrange
        IdentityUserName seen = default;

        // Act
        Builder.AddUser("ada@contoso.example", user =>
        {
            seen = user.Name;
            user.DisplayName = "Ada Lovelace";
            user.GivenName = "Ada";
            user.FamilyName = "Lovelace";
            user.Email = "ada@contoso.example";
            user.IsEmailVerified = true;
        });

        // Assert
        seen.Value.ShouldBe("ada@contoso.example");
        IdentityUser defined = _recording.Users.ShouldHaveSingleItem();
        defined.DisplayName.ShouldBe("Ada Lovelace");
        defined.GivenName.ShouldBe("Ada");
        defined.FamilyName.ShouldBe("Lovelace");
        defined.Email.ShouldBe("ada@contoso.example");
        defined.IsEmailVerified.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - AddUser: Should keep a user's identifier across a change of name")]
    public void AddUser_WithAnIdentifierAndACallback_ShouldKeepTheIdentifierUnderANewName()
    {
        // Arrange
        IdentityUserId id = IdentityUserId.FromName("ada@contoso.example");
        IdentityUserId seen = default;

        // Act
        IIdentityHubApplicationBuilder returned = Builder.AddUser(id, user =>
        {
            seen = user.Id;
            user.Name = "ada.lovelace@contoso.example";
        });

        // Assert
        returned.ShouldBeSameAs(_recording);
        seen.ShouldBe(id);
        IdentityUser defined = _recording.Users.ShouldHaveSingleItem();
        defined.Id.ShouldBe(id);
        defined.Name.Value.ShouldBe("ada.lovelace@contoso.example");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - AddUser: Should refuse a default name, a default identifier, and a null callback for an identifier")]
    public void AddUser_WithInvalidArguments_ShouldThrow()
    {
        // Act
        ArgumentException noName = Should.Throw<ArgumentException>(() => Builder.AddUser(default(IdentityUserName)));
        ArgumentException noId = Should.Throw<ArgumentException>(() => Builder.AddUser(default(IdentityUserId), _ => { }));
        ArgumentNullException noCallback = Should.Throw<ArgumentNullException>(
            () => Builder.AddUser(IdentityUserId.FromName("ada"), null!));

        // Assert
        noName.ParamName.ShouldBe("name");
        noId.ParamName.ShouldBe("id");
        noCallback.ParamName.ShouldBe("configure");
        _recording.Users.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - AddUser: Should refuse a user that breaks a rule on the line that declares it")]
    public void AddUser_WithAUserThatBreaksARule_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException noNameGiven = Should.Throw<ArgumentException>(
            () => Builder.AddUser(IdentityUserId.FromName("ada"), _ => { }));
        ArgumentException unverifiable = Should.Throw<ArgumentException>(
            () => Builder.AddUser("ada", user => user.IsEmailVerified = true));
        ArgumentException nameCleared = Should.Throw<ArgumentException>(
            () => Builder.AddUser("ada", user => user.Name = default));

        // Assert
        noNameGiven.ParamName.ShouldBe("descriptor");
        unverifiable.ParamName.ShouldBe("descriptor");
        nameCleared.ParamName.ShouldBe("descriptor");
        _recording.Users.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - AddGroup: Should define a group from a name alone, with the identifier derived from it")]
    public void AddGroup_WithANameAlone_ShouldDefineTheGroupUnderTheDerivedIdentifier()
    {
        // Act
        IIdentityHubApplicationBuilder returned = Builder.AddGroup("Orders-Admins");

        // Assert
        returned.ShouldBeSameAs(_recording);
        IdentityGroup group = _recording.Groups.ShouldHaveSingleItem();
        group.Id.ShouldBe(IdentityGroupId.FromName("orders-admins"));
        group.Name.Value.ShouldBe("Orders-Admins");
        group.DisplayName.ShouldBe("Orders-Admins");
        group.Description.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - AddGroup: Should hand the callback a descriptor that already holds the name")]
    public void AddGroup_WithANameAndACallback_ShouldFillTheRestOfTheGroup()
    {
        // Arrange
        IdentityGroupName seen = default;

        // Act
        Builder.AddGroup("orders-admins", group =>
        {
            seen = group.Name;
            group.DisplayName = "Orders administrators";
            group.Description = "Everyone who may change an order.";
        });

        // Assert
        seen.Value.ShouldBe("orders-admins");
        IdentityGroup defined = _recording.Groups.ShouldHaveSingleItem();
        defined.DisplayName.ShouldBe("Orders administrators");
        defined.Description.ShouldBe("Everyone who may change an order.");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - AddGroup: Should keep a group's identifier across a change of name")]
    public void AddGroup_WithAnIdentifierAndACallback_ShouldKeepTheIdentifierUnderANewName()
    {
        // Arrange
        IdentityGroupId id = IdentityGroupId.FromName("orders-admins");
        IdentityGroupId seen = default;

        // Act
        Builder.AddGroup(id, group =>
        {
            seen = group.Id;
            group.Name = "orders-owners";
        });

        // Assert
        seen.ShouldBe(id);
        IdentityGroup defined = _recording.Groups.ShouldHaveSingleItem();
        defined.Id.ShouldBe(id);
        defined.Name.Value.ShouldBe("orders-owners");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - AddGroup: Should refuse a default name, a default identifier, a null callback, and a group that breaks a rule")]
    public void AddGroup_WithInvalidArguments_ShouldThrow()
    {
        // Act
        ArgumentException noName = Should.Throw<ArgumentException>(() => Builder.AddGroup(default(IdentityGroupName)));
        ArgumentException noId = Should.Throw<ArgumentException>(() => Builder.AddGroup(default(IdentityGroupId), _ => { }));
        ArgumentNullException noCallback = Should.Throw<ArgumentNullException>(
            () => Builder.AddGroup(IdentityGroupId.FromName("everyone"), null!));
        ArgumentException noNameGiven = Should.Throw<ArgumentException>(
            () => Builder.AddGroup(IdentityGroupId.FromName("everyone"), _ => { }));
        ArgumentException blankDisplayName = Should.Throw<ArgumentException>(
            () => Builder.AddGroup("everyone", group => group.DisplayName = "   "));

        // Assert
        noName.ParamName.ShouldBe("name");
        noId.ParamName.ShouldBe("id");
        noCallback.ParamName.ShouldBe("configure");
        noNameGiven.ParamName.ShouldBe("descriptor");
        blankDisplayName.ParamName.ShouldBe("descriptor");
        _recording.Groups.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - AddUser, AddGroup: Should chain on the builder")]
    public void AddUserAndAddGroup_Chained_ShouldDefineEveryObject()
    {
        // Act
        Builder
            .AddGroup("orders-admins")
            .AddGroup("everyone", group => group.Description = "Every signed-in user.")
            .AddUser("ada@contoso.example")
            .AddUser("grace@contoso.example", user => user.IsEnabled = false);

        // Assert
        _recording.Groups.Count.ShouldBe(2);
        _recording.Users.Count.ShouldBe(2);
        _recording.Users[1].IsEnabled.ShouldBeFalse();
    }
}
