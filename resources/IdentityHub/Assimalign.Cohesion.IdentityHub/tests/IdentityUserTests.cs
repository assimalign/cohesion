using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// A user is created whole from a descriptor, or not at all.
/// </summary>
public class IdentityUserTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a null user descriptor")]
    public void Create_WithANullDescriptor_ShouldThrowArgumentNullException()
    {
        // Act
        ArgumentNullException exception = Should.Throw<ArgumentNullException>(() => IdentityUser.Create(null!));

        // Assert
        exception.ParamName.ShouldBe("descriptor");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should derive the identifier and the display name from the user's name")]
    public void Create_WithOnlyAName_ShouldDeriveTheRest()
    {
        // Act
        IdentityUser user = IdentityUser.Create(new() { Name = "Ada@Contoso.example" });

        // Assert
        user.Id.ShouldBe(IdentityUserId.FromName("ada@contoso.example"));
        user.Name.Value.ShouldBe("Ada@Contoso.example");
        user.DisplayName.ShouldBe("Ada@Contoso.example");
        user.GivenName.ShouldBeNull();
        user.FamilyName.ShouldBeNull();
        user.Email.ShouldBeNull();
        user.IsEmailVerified.ShouldBeFalse();
        user.IsEnabled.ShouldBeTrue();
        user.IsLocked.ShouldBeFalse();
        user.Version.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should keep everything a user descriptor gives")]
    public void Create_WithAFullDescriptor_ShouldKeepEveryMember()
    {
        // Arrange
        IdentityUserId id = IdentityUserId.New();

        // Act
        IdentityUser user = IdentityUser.Create(new()
        {
            Id = id,
            Name = "ada",
            DisplayName = "Ada Lovelace",
            GivenName = "Ada",
            FamilyName = "Lovelace",
            Email = "ada@contoso.example",
            IsEmailVerified = true,
            IsEnabled = false,
        });

        // Assert
        user.Id.ShouldBe(id);
        user.Name.Value.ShouldBe("ada");
        user.DisplayName.ShouldBe("Ada Lovelace");
        user.GivenName.ShouldBe("Ada");
        user.FamilyName.ShouldBe("Lovelace");
        user.Email.ShouldBe("ada@contoso.example");
        user.IsEmailVerified.ShouldBeTrue();
        user.IsEnabled.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should treat empty optional text as not given")]
    public void Create_WithEmptyOptionalText_ShouldTreatItAsAbsent()
    {
        // Act
        IdentityUser user = IdentityUser.Create(new()
        {
            Name = "ada",
            DisplayName = "",
            GivenName = "",
            FamilyName = "",
            Email = "",
        });

        // Assert
        user.DisplayName.ShouldBe("ada");
        user.GivenName.ShouldBeNull();
        user.FamilyName.ShouldBeNull();
        user.Email.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should snapshot the descriptor")]
    public void Create_WhenTheDescriptorChangesAfterwards_ShouldNotChangeTheUser()
    {
        // Arrange
        var descriptor = new IdentityUserDescriptor { Name = "ada", DisplayName = "Ada" };
        IdentityUser user = IdentityUser.Create(descriptor);

        // Act
        descriptor.DisplayName = "Someone else";
        descriptor.IsEnabled = false;

        // Assert
        user.DisplayName.ShouldBe("Ada");
        user.IsEnabled.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a user with no name")]
    public void Create_WithNoName_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => IdentityUser.Create(new() { Id = IdentityUserId.New(), DisplayName = "Ada" }));

        // Assert
        exception.ParamName.ShouldBe("descriptor");
        exception.Message.ShouldContain("user name is required", Case.Insensitive);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a display name, given name, or family name that is blank, holds a control character, or is too long")]
    [InlineData("display", "   ")]
    [InlineData("display", "Ada\nLovelace")]
    [InlineData("display", "Ada\u0000")]
    [InlineData("given", " ")]
    [InlineData("given", "Ada\tB")]
    [InlineData("family", " ")]
    [InlineData("family", "Love\rlace")]
    public void Create_WithInvalidText_ShouldThrowArgumentExceptionNamingTheUser(string member, string value)
    {
        // Arrange
        var descriptor = new IdentityUserDescriptor { Name = "ada" };
        switch (member)
        {
            case "display":
                descriptor.DisplayName = value;
                break;
            case "given":
                descriptor.GivenName = value;
                break;
            default:
                descriptor.FamilyName = value;
                break;
        }

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => IdentityUser.Create(descriptor));

        // Assert
        exception.ParamName.ShouldBe("descriptor");
        exception.Message.ShouldContain($"'ada ({IdentityUserId.FromName("ada")})'", Case.Sensitive);
        exception.Message.ShouldContain(member, Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should accept text at its limit and refuse it beyond")]
    public void Create_AtTheTextLimits_ShouldAcceptTheLimitAndRefuseBeyondIt()
    {
        // Arrange
        string displayName = new('d', IdentityUser.MaxDisplayNameLength);
        string personName = new('p', IdentityUser.MaxPersonNameLength);

        // Act
        IdentityUser atLimit = IdentityUser.Create(new()
        {
            Name = "ada",
            DisplayName = displayName,
            GivenName = personName,
            FamilyName = personName,
        });

        // Assert
        IdentityUser.MaxDisplayNameLength.ShouldBe(128);
        IdentityUser.MaxPersonNameLength.ShouldBe(64);
        IdentityUser.MaxEmailLength.ShouldBe(254);
        atLimit.DisplayName.Length.ShouldBe(128);
        Should.Throw<ArgumentException>(() => IdentityUser.Create(new() { Name = "ada", DisplayName = displayName + "d" }));
        Should.Throw<ArgumentException>(() => IdentityUser.Create(new() { Name = "ada", GivenName = personName + "p" }));
        Should.Throw<ArgumentException>(() => IdentityUser.Create(new() { Name = "ada", FamilyName = personName + "p" }));
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Create: Should accept an email address by its shape")]
    [InlineData("a@b")]
    [InlineData("ada@contoso.example")]
    [InlineData("ada.lovelace+ops@sub.contoso.example")]
    [InlineData("ädä@contoso.example")]
    public void Create_WithAnEmailAddress_ShouldKeepIt(string email)
    {
        // Act
        IdentityUser user = IdentityUser.Create(new() { Name = "ada", Email = email });

        // Assert
        user.Email.ShouldBe(email);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse text that does not have the shape of an email address")]
    [InlineData("ada")]
    [InlineData("@contoso.example")]
    [InlineData("ada@")]
    [InlineData("ada@contoso@example")]
    [InlineData("ada lovelace@contoso.example")]
    [InlineData("ada@contoso.example\n")]
    [InlineData("a@")]
    public void Create_WithTextThatIsNoEmailAddress_ShouldThrowWithoutEchoingIt(string email)
    {
        // Act: the user's name shares no text with the refused address, so the message's label
        // for the user cannot be mistaken for an echo of it.
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => IdentityUser.Create(new() { Name = "grace", Email = email }));

        // Assert
        exception.ParamName.ShouldBe("descriptor");
        exception.Message.ShouldContain("email", Case.Insensitive);
        exception.Message.ShouldNotContain(email, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse an email address longer than its limit")]
    public void Create_WithAnEmailAddressBeyondItsLimit_ShouldThrowArgumentException()
    {
        // Arrange
        string atLimit = new string('a', IdentityUser.MaxEmailLength - 2) + "@b";
        string beyond = "a" + atLimit;

        // Act
        IdentityUser user = IdentityUser.Create(new() { Name = "ada", Email = atLimit });

        // Assert
        user.Email!.Length.ShouldBe(254);
        Should.Throw<ArgumentException>(() => IdentityUser.Create(new() { Name = "ada", Email = beyond }));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse an email address marked verified when none is given")]
    public void Create_WithAVerifiedEmailAndNoAddress_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => IdentityUser.Create(new() { Name = "ada", IsEmailVerified = true }));

        // Assert
        exception.ParamName.ShouldBe("descriptor");
        exception.Message.ShouldContain("verified", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - From: Should describe a user so that it creates an equal one")]
    public void From_WithAUser_ShouldRoundTripEveryMember()
    {
        // Arrange
        IdentityUser user = IdentityUser.Create(new()
        {
            Id = IdentityUserId.New(),
            Name = "ada",
            DisplayName = "Ada Lovelace",
            GivenName = "Ada",
            FamilyName = "Lovelace",
            Email = "ada@contoso.example",
            IsEmailVerified = true,
            IsEnabled = false,
        });

        // Act
        IdentityUser copy = IdentityUser.Create(IdentityUserDescriptor.From(user));

        // Assert
        copy.Id.ShouldBe(user.Id);
        copy.Name.ShouldBe(user.Name);
        copy.DisplayName.ShouldBe(user.DisplayName);
        copy.GivenName.ShouldBe(user.GivenName);
        copy.FamilyName.ShouldBe(user.FamilyName);
        copy.Email.ShouldBe(user.Email);
        copy.IsEmailVerified.ShouldBe(user.IsEmailVerified);
        copy.IsEnabled.ShouldBe(user.IsEnabled);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - From: Should keep a user's identifier when its name changes")]
    public void From_WhenTheNameChanges_ShouldKeepTheIdentifier()
    {
        // Arrange
        IdentityUser user = IdentityUser.Create(new() { Name = "ada" });
        IdentityUserDescriptor descriptor = IdentityUserDescriptor.From(user);

        // Act
        descriptor.Name = "ada.lovelace";
        IdentityUser renamed = IdentityUser.Create(descriptor);

        // Assert
        renamed.Id.ShouldBe(IdentityUserId.FromName("ada"));
        renamed.Name.Value.ShouldBe("ada.lovelace");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - From: Should refuse a null user")]
    public void From_WithANullUser_ShouldThrowArgumentNullException()
    {
        // Act
        ArgumentNullException exception = Should.Throw<ArgumentNullException>(() => IdentityUserDescriptor.From(null!));

        // Assert
        exception.ParamName.ShouldBe("user");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Descriptor: Should start enabled with nothing else set")]
    public void Descriptor_WhenNew_ShouldBeEnabledAndEmpty()
    {
        // Act
        var descriptor = new IdentityUserDescriptor();

        // Assert
        descriptor.IsEnabled.ShouldBeTrue();
        descriptor.IsEmailVerified.ShouldBeFalse();
        descriptor.Id.ShouldBe(IdentityUserId.Empty);
        descriptor.Name.Value.ShouldBeNull();
    }
}
