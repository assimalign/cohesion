using System;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Contains unit tests for the authentication context model.
/// </summary>
public sealed class AuthenticationContextTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Context: Materialization should snapshot statement data")]
    public void Constructor_WhenConstructed_ShouldSnapshotStatementData()
    {
        // Arrange — the data a SAML AuthnStatement / OIDC ID token carries about the event.
        var authenticatedAt = new DateTimeOffset(2026, 7, 3, 9, 30, 0, TimeSpan.Zero);
        var descriptor = new AuthenticationContextDescriptor
        {
            AuthenticatedAt = authenticatedAt,
            ContextClass = "urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport",
            SessionExpiresAt = authenticatedAt.AddHours(8),
        };
        descriptor.Methods.Add("pwd");
        descriptor.Methods.Add("otp");
        descriptor.AuthenticatingAuthorities.Add("https://proxy-idp.example");
        descriptor.ProviderSessionIds.Add("session-index-1");
        descriptor.ProviderSessionIds.Add("session-index-2");
        descriptor.Properties["subject-locality"] = "203.0.113.7";

        // Act
        var context = new AuthenticationContext(descriptor);
        descriptor.Methods.Add("late-mutation");

        // Assert
        context.AuthenticatedAt.ShouldBe(authenticatedAt);
        context.ContextClass.ShouldBe("urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport");
        context.Methods.ShouldBe(["pwd", "otp"]);
        context.AuthenticatingAuthorities.ShouldBe(["https://proxy-idp.example"]);
        context.ProviderSessionIds.ShouldBe(["session-index-1", "session-index-2"]);
        context.SessionExpiresAt.ShouldBe(authenticatedAt.AddHours(8));
        context.Properties["subject-locality"].AsString().ShouldBe("203.0.113.7");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Context: Materialization should reject invalid entries")]
    public void Constructor_WhenEntriesAreInvalid_ShouldThrow()
    {
        var withBlankMethod = new AuthenticationContextDescriptor();
        withBlankMethod.Methods.Add("  ");

        var withUndefinedProperty = new AuthenticationContextDescriptor();
        withUndefinedProperty.Properties["risk"] = default;

        Should.Throw<ArgumentException>(() => new AuthenticationContext(withBlankMethod));
        Should.Throw<ArgumentException>(() => new AuthenticationContext(withUndefinedProperty));
        Should.Throw<ArgumentNullException>(() => new AuthenticationContext(null!));
    }
}
