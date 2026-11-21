using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityClientScopeTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should keep the name, the display name, and the description")]
    public void Create_WithEveryPart_ShouldKeepEach()
    {
        // Act
        IdentityClientScope scope = IdentityClientScope.Create("orders.read", "Read orders", "Read the orders of the signed-in user.\nNothing else.");

        // Assert
        scope.Name.Value.ShouldBe("orders.read");
        scope.DisplayName.ShouldBe("Read orders");
        scope.Description.ShouldBe("Read the orders of the signed-in user.\nNothing else.");
        scope.ToString().ShouldBe("orders.read");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should show the name when no display name is given")]
    public void Create_WithNoDisplayName_ShouldShowTheName()
    {
        // Act
        IdentityClientScope scope = IdentityClientScope.Create("orders.read", description: string.Empty);

        // Assert
        scope.DisplayName.ShouldBe("orders.read");
        scope.Description.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a default name")]
    public void Create_WithADefaultName_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => IdentityClientScope.Create(default));

        // Assert
        exception.ParamName.ShouldBe("name");
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a blank display name and one with a control character")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Read\norders")]
    [InlineData("Read\u0000orders")]
    public void Create_WithADisplayNameOutsideItsRule_ShouldThrowArgumentException(string displayName)
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => IdentityClientScope.Create("orders.read", displayName));

        // Assert
        exception.ParamName.ShouldBe("displayName");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a display name or a description longer than its limit")]
    public void Create_BeyondALengthLimit_ShouldThrowArgumentException()
    {
        // Arrange
        string longestName = new('n', IdentityClient.MaxDisplayNameLength);
        string longestDescription = new('d', IdentityClient.MaxDescriptionLength);

        // Act
        IdentityClientScope atLimit = IdentityClientScope.Create("orders.read", longestName, longestDescription);
        ArgumentException longName = Should.Throw<ArgumentException>(
            () => IdentityClientScope.Create("orders.read", longestName + "n"));
        ArgumentException longDescription = Should.Throw<ArgumentException>(
            () => IdentityClientScope.Create("orders.read", description: longestDescription + "d"));
        ArgumentException controlDescription = Should.Throw<ArgumentException>(
            () => IdentityClientScope.Create("orders.read", description: "bell\u0007"));

        // Assert
        atLimit.DisplayName.Length.ShouldBe(128);
        atLimit.Description!.Length.ShouldBe(256);
        longName.ParamName.ShouldBe("displayName");
        longDescription.ParamName.ShouldBe("description");
        controlDescription.ParamName.ShouldBe("description");
    }
}
