using System;
using System.Security.Claims;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Authentication.Tests;

public class AuthenticationModelTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - AuthenticateResult: Success carries the ticket and principal")]
    public void AuthenticateResult_Success_CarriesTicket()
    {
        // Arrange
        ClaimsPrincipal principal = new(new ClaimsIdentity("Test"));
        AuthenticationTicket ticket = new(principal, null, "Test");

        // Act
        AuthenticateResult result = AuthenticateResult.Success(ticket);

        // Assert
        result.Succeeded.ShouldBeTrue();
        result.None.ShouldBeFalse();
        result.Failure.ShouldBeNull();
        result.Principal.ShouldBeSameAs(principal);
        result.Ticket.ShouldBeSameAs(ticket);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - AuthenticateResult: NoResult is neither success nor failure")]
    public void AuthenticateResult_NoResult_IsNeitherSuccessNorFailure()
    {
        // Act
        AuthenticateResult result = AuthenticateResult.NoResult();

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.None.ShouldBeTrue();
        result.Failure.ShouldBeNull();
        result.Principal.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - AuthenticateResult: Fail carries the failure and is not None")]
    public void AuthenticateResult_Fail_CarriesFailure()
    {
        // Act
        AuthenticateResult result = AuthenticateResult.Fail("bad token");

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.None.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure!.Message.ShouldBe("bad token");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - AuthenticationTicket: rejects a null principal")]
    public void AuthenticationTicket_NullPrincipal_Throws()
        => Should.Throw<ArgumentNullException>(() => new AuthenticationTicket(null!, null, "Test"));

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - AuthenticationTicket: rejects a blank scheme")]
    public void AuthenticationTicket_BlankScheme_Throws()
        => Should.Throw<ArgumentException>(
            () => new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity()), null, " "));

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - AuthenticationProperties: Clone is a deep copy")]
    public void AuthenticationProperties_Clone_IsDeepCopy()
    {
        // Arrange
        AuthenticationProperties original = new()
        {
            IsPersistent = true,
            RedirectUri = "/home",
            ExpiresUtc = DateTimeOffset.UnixEpoch,
        };
        original.Items["k"] = "v";

        // Act
        AuthenticationProperties clone = original.Clone();
        clone.Items["k"] = "changed";
        clone.RedirectUri = "/other";

        // Assert
        original.Items["k"].ShouldBe("v");
        original.RedirectUri.ShouldBe("/home");
        clone.IsPersistent.ShouldBe(true);
        clone.ExpiresUtc.ShouldBe(DateTimeOffset.UnixEpoch);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - AuthenticationOptions: specific defaults fall back to DefaultScheme")]
    public void AuthenticationOptions_SpecificDefaults_FallBackToDefaultScheme()
    {
        // Arrange
        AuthenticationOptions options = new() { DefaultScheme = "Base" };

        // Assert
        options.ResolveDefaultAuthenticateScheme().ShouldBe("Base");
        options.ResolveDefaultChallengeScheme().ShouldBe("Base");
        options.ResolveDefaultForbidScheme().ShouldBe("Base");
        options.ResolveDefaultSignInScheme().ShouldBe("Base");
        options.ResolveDefaultSignOutScheme().ShouldBe("Base");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - AuthenticationOptions: forbid falls back to challenge scheme")]
    public void AuthenticationOptions_ForbidFallsBackToChallenge()
    {
        // Arrange
        AuthenticationOptions options = new()
        {
            DefaultScheme = "Base",
            DefaultChallengeScheme = "Bearer",
        };

        // Assert
        options.ResolveDefaultForbidScheme().ShouldBe("Bearer");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - AuthenticationOptions: rejects duplicate scheme names")]
    public void AuthenticationOptions_DuplicateScheme_Throws()
    {
        // Arrange
        AuthenticationOptions options = new();
        options.AddScheme(new AuthenticationScheme("Test", null, () => throw new InvalidOperationException()));

        // Act + Assert
        Should.Throw<InvalidOperationException>(
            () => options.AddScheme(new AuthenticationScheme("Test", null, () => throw new InvalidOperationException())));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - AuthenticationScheme: rejects a null handler factory")]
    public void AuthenticationScheme_NullFactory_Throws()
        => Should.Throw<ArgumentNullException>(() => new AuthenticationScheme("Test", null, null!));
}
