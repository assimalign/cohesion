using System;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Contains unit tests for the authentication session model: lifecycle validity, single
/// logout correlation data, and invalid-state guards.
/// </summary>
public sealed class AuthenticationSessionTests
{
    private static readonly DateTimeOffset now = new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

    private static AuthenticationSessionDescriptor CreateDescriptor()
    {
        var descriptor = new AuthenticationSessionDescriptor
        {
            SessionId = "session-1",
            Subject = new SubjectIdentifier("user-1", issuer: "https://idp.example"),
            SubjectKind = IdentityKind.User,
            Protocol = AuthenticationProtocol.Saml2,
            Issuer = "https://idp.example",
            CreatedAt = now,
            ExpiresAt = now.AddHours(8),
            State = AuthenticationSessionState.Active,
        };
        descriptor.ProviderSessionIds.Add("index-a");
        return descriptor;
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Session: Model should carry single-logout correlation data")]
    public void Constructor_WhenConstructed_ShouldCarrySingleLogoutCorrelationData()
    {
        // Arrange — SAML re-authentication adds a second SessionIndex to the same session.
        var descriptor = CreateDescriptor();
        descriptor.ProviderSessionIds.Add("index-b");

        // Act
        var session = new AuthenticationSession(descriptor);

        // Assert — an inbound LogoutRequest(issuer, sessionIndex) can be matched.
        session.Issuer.ShouldBe("https://idp.example");
        session.Protocol.ShouldBe(AuthenticationProtocol.Saml2);
        session.ProviderSessionIds.ShouldBe(["index-a", "index-b"]);
        session.Subject.Value.ShouldBe("user-1");
        session.SubjectKind.ShouldBe(IdentityKind.User);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Session: IsActive should combine state and window")]
    public void IsActive_WhenEvaluated_ShouldCombineStateAndWindow()
    {
        var active = new AuthenticationSession(CreateDescriptor());

        active.IsActive(now).ShouldBeTrue();
        active.IsActive(now.AddHours(1)).ShouldBeTrue();
        active.IsActive(now.AddMinutes(-1)).ShouldBeFalse();       // before creation
        active.IsActive(now.AddHours(8)).ShouldBeFalse();          // expiry is exclusive
        active.IsActive(now.AddHours(9)).ShouldBeFalse();          // after expiry

        var terminatedDescriptor = CreateDescriptor();
        terminatedDescriptor.State = AuthenticationSessionState.Terminated;
        var terminated = new AuthenticationSession(terminatedDescriptor);

        terminated.IsActive(now).ShouldBeFalse();

        var unboundedDescriptor = CreateDescriptor();
        unboundedDescriptor.ExpiresAt = null;
        var unbounded = new AuthenticationSession(unboundedDescriptor);

        unbounded.IsActive(now.AddYears(10)).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Session: A defaulted state should never be active")]
    public void IsActive_WhenStateWasNeverAssigned_ShouldBeFalse()
    {
        // A store rehydration that forgets to map the persisted state must fail closed —
        // the same rule IdentityCredentialState applies to credentials.
        var descriptor = CreateDescriptor();
        descriptor.State = default;

        var session = new AuthenticationSession(descriptor);

        session.State.ShouldBe(AuthenticationSessionState.Unknown);
        session.IsActive(now).ShouldBeFalse();
        ((int)AuthenticationSessionState.Unknown).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Session: Wire-sourced expiry at or before creation should materialize as never active")]
    public void Constructor_WhenExpiryPrecedesCreation_ShouldMaterializeAsNeverActive()
    {
        // A SAML SessionNotOnOrAfter comes from the provider's clock and may precede the
        // locally observed completion instant under skew; that is valid wire data, not a
        // contract violation, so the sign-in path must not crash on it.
        var descriptor = CreateDescriptor();
        descriptor.ExpiresAt = descriptor.CreatedAt!.Value.AddSeconds(-1);

        var session = new AuthenticationSession(descriptor);

        session.IsActive(descriptor.CreatedAt!.Value).ShouldBeFalse();
        session.IsActive(descriptor.CreatedAt!.Value.AddHours(1)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Session: Materialization should reject invalid descriptors")]
    public void Constructor_WhenDescriptorIsInvalid_ShouldThrow()
    {
        var missingSessionId = CreateDescriptor();
        missingSessionId.SessionId = null;

        var missingSubject = CreateDescriptor();
        missingSubject.Subject = null;

        var missingCreatedAt = CreateDescriptor();
        missingCreatedAt.CreatedAt = null;

        Should.Throw<IdentityModelException>(() => new AuthenticationSession(missingSessionId));
        Should.Throw<IdentityModelException>(() => new AuthenticationSession(missingSubject));
        Should.Throw<IdentityModelException>(() => new AuthenticationSession(missingCreatedAt));
        Should.Throw<ArgumentNullException>(() => new AuthenticationSession(null!));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Session: Descriptor mutation after materialization should not leak")]
    public void Constructor_WhenDescriptorMutatesAfterwards_ShouldNotChange()
    {
        // Arrange
        var descriptor = CreateDescriptor();
        descriptor.Properties["tenant"] = "cohesion";

        var session = new AuthenticationSession(descriptor);

        // Act
        descriptor.ProviderSessionIds.Add("late-index");
        descriptor.Properties["tenant"] = "mutated";

        // Assert
        session.ProviderSessionIds.Count.ShouldBe(1);
        session.Properties["tenant"].AsString().ShouldBe("cohesion");
    }
}
