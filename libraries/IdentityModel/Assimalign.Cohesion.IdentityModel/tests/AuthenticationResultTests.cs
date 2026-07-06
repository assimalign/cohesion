using System;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Contains unit tests for the authentication result model: the success/failure invariant,
/// provenance, and audit completeness.
/// </summary>
public sealed class AuthenticationResultTests
{
    private static readonly DateTimeOffset now = new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

    private static IIdentitySubject CreateSubject()
    {
        return new IdentitySubject(new IdentitySubjectDescriptor
        {
            Kind = IdentityKind.User,
            Identifier = new SubjectIdentifier("user-1", issuer: "https://op.example"),
        });
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Result: Success should carry subject, provenance, and context")]
    public void Success_WhenConstructed_ShouldCarrySubjectProvenanceAndContext()
    {
        // Arrange — what an OIDC RP persists after a code+PKCE sign-in.
        var contextDescriptor = new AuthenticationContextDescriptor { AuthenticatedAt = now.AddSeconds(-5) };
        contextDescriptor.ProviderSessionIds.Add("op-sid-1");

        var descriptor = new AuthenticationResultDescriptor
        {
            Subject = CreateSubject(),
            Protocol = AuthenticationProtocol.OpenIdConnect,
            CompletedAt = now,
            Issuer = "https://op.example",
            Audience = "client-app-1",
            EvidenceId = "jti-8842",
            Context = new AuthenticationContext(contextDescriptor),
        };

        // Act
        var result = new AuthenticationResult(descriptor);

        // Assert
        result.Succeeded.ShouldBeTrue();
        result.Subject.ShouldNotBeNull();
        result.Failure.ShouldBeNull();
        result.Protocol.ShouldBe(AuthenticationProtocol.OpenIdConnect);
        result.Issuer.ShouldBe("https://op.example");
        result.Audience.ShouldBe("client-app-1");
        result.EvidenceId.ShouldBe("jti-8842");
        result.Context!.ProviderSessionIds.ShouldBe(["op-sid-1"]);
        result.CompletedAt.ShouldBe(now);

        // The nullable-flow contract: inside a Succeeded branch, Subject dereferences
        // without suppression.
        if (result.Succeeded)
        {
            result.Subject.Identifier.Value.ShouldBe("user-1");
        }
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Result: Failure should preserve who failed and the wire error")]
    public void Failed_WhenConstructed_ShouldPreserveAttemptAndWireError()
    {
        // Arrange — a failed sign-in for a KNOWN user must remain auditable.
        var descriptor = new AuthenticationResultDescriptor
        {
            Failure = new AuthenticationFailure(
                AuthenticationFailureCodes.InvalidCredentials,
                "The provided password is incorrect.",
                originalCode: "invalid_grant",
                errorUri: "https://op.example/errors/invalid_grant"),
            AttemptedSubject = new SubjectIdentifier("alice", issuer: "https://op.example"),
            Protocol = AuthenticationProtocol.OAuth2,
            CompletedAt = now,
            CredentialId = "password-primary",
        };

        // Act
        var result = new AuthenticationResult(descriptor);

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.Subject.ShouldBeNull();
        result.Failure!.Code.ShouldBe("invalid_credentials");
        result.Failure.OriginalCode.ShouldBe("invalid_grant");
        result.Failure.ErrorUri.ShouldBe("https://op.example/errors/invalid_grant");
        result.AttemptedSubject!.Value.ShouldBe("alice");
        result.CredentialId.ShouldBe("password-primary");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Result: Exactly one of subject and failure is required")]
    public void Constructor_WhenSubjectAndFailureDisagree_ShouldThrow()
    {
        var neither = new AuthenticationResultDescriptor { CompletedAt = now };
        var both = new AuthenticationResultDescriptor
        {
            Subject = CreateSubject(),
            Failure = new AuthenticationFailure(AuthenticationFailureCodes.Unknown, "?"),
            CompletedAt = now,
        };
        var missingCompletion = new AuthenticationResultDescriptor { Subject = CreateSubject() };

        Should.Throw<IdentityModelException>(() => new AuthenticationResult(neither));
        Should.Throw<IdentityModelException>(() => new AuthenticationResult(both));
        Should.Throw<IdentityModelException>(() => new AuthenticationResult(missingCompletion));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Result: Convenience factories should enforce their invariants")]
    public void Factories_WhenInvoked_ShouldEnforceInvariants()
    {
        var success = AuthenticationResult.Success(CreateSubject(), AuthenticationProtocol.OpenIdConnect, now);
        var failure = AuthenticationResult.Failed(
            new AuthenticationFailure(AuthenticationFailureCodes.SubjectNotFound, "No such subject."),
            AuthenticationProtocol.Saml2,
            now);

        success.Succeeded.ShouldBeTrue();
        failure.Succeeded.ShouldBeFalse();
        failure.Protocol.ShouldBe(AuthenticationProtocol.Saml2);

        Should.Throw<ArgumentNullException>(() => AuthenticationResult.Success(null!, AuthenticationProtocol.OpenIdConnect, now));
        Should.Throw<ArgumentNullException>(() => AuthenticationResult.Failed(null!, AuthenticationProtocol.Saml2, now));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Result: Failure guards should reject blank members")]
    public void AuthenticationFailure_WhenGivenBlankMembers_ShouldThrow()
    {
        Should.Throw<ArgumentException>(() => new AuthenticationFailure("", "message"));
        Should.Throw<ArgumentException>(() => new AuthenticationFailure("code", " "));
    }
}
