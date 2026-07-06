using System;
using System.Collections.Generic;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;
using Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect.Tests;

/// <summary>
/// Contains unit tests and Back-Channel Logout 1.0 fixtures for the logout contracts,
/// including the end-to-end single-logout correlation join.
/// </summary>
public sealed class OpenIdConnectLogoutTests
{
    private static readonly DateTimeOffset now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    private static OpenIdConnectLogoutTokenDescriptor CreateConformantLogoutToken()
    {
        var descriptor = new OpenIdConnectLogoutTokenDescriptor
        {
            Issuer = "https://server.example.com",
            Subject = "24400320",
            IssuedAt = now.AddSeconds(-5),
            ExpiresAt = now.AddMinutes(2),
            JwtId = "bWJq",
            SessionId = "08a5019c-17e1-4977-8f42-65a12843ea02",
            RawToken = "eyJhbGciOiJSUzI1NiJ9.logout.signature",
        };
        descriptor.Audiences.Add("s6BhdRkqt3");
        descriptor.Events[OpenIdConnectEventTypes.BackChannelLogout] =
            IdentityClaimValue.FromObject(Array.Empty<KeyValuePair<string, IdentityClaimValue>>());
        return descriptor;
    }

    private static OpenIdConnectLogoutTokenValidationOptions CreateOptions()
        => new(validateAt: now)
        {
            ExpectedIssuer = "https://server.example.com",
            ExpectedAudience = "s6BhdRkqt3",
        };

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: RP-initiated logout should separate who from why")]
    public void LogoutRequest_WhenConstructed_ShouldSeparateWhoFromWhy()
    {
        // Arrange — logout_hint identifies WHO; the shared Reason stays the WHY category.
        var request = new OpenIdConnectLogoutRequest(new OpenIdConnectLogoutRequestDescriptor
        {
            ClientId = "s6BhdRkqt3",
            IdTokenHint = "eyJhbGciOiJSUzI1NiJ9.payload.signature",
            LogoutHint = "user@example.com",
            PostLogoutRedirectUri = "https://rp.example/signed-out",
            CorrelationState = "logout-state-1",
        });

        // Assert
        request.ClientId.ShouldBe("s6BhdRkqt3");
        request.Issuer.ShouldBe("s6BhdRkqt3");
        request.IdTokenHint.ShouldNotBeNull();
        request.LogoutHint.ShouldBe("user@example.com");
        request.Reason.ShouldBeNull();
        request.CorrelationState.ShouldBe("logout-state-1");

        var response = new OpenIdConnectLogoutResponse(new OpenIdConnectLogoutResponseDescriptor
        {
            CorrelationState = "logout-state-1",
            Status = ProtocolResponseStatus.Success,
        });

        response.CorrelationState.ShouldBe("logout-state-1");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: A conformant logout token should validate clean")]
    public void LogoutToken_WhenConformant_ShouldValidateClean()
    {
        var token = new OpenIdConnectLogoutToken(CreateConformantLogoutToken());

        token.Validate(CreateOptions()).Succeeded.ShouldBeTrue();
        token.Events.ContainsKey(OpenIdConnectEventTypes.BackChannelLogout).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Logout token validation should implement the section 2.6 rules")]
    public void LogoutToken_WhenNonConformant_ShouldReportTheSpecRules()
    {
        // The events member must carry the logout event with an OBJECT payload — the
        // non-conformant array form some early implementations emitted must be rejected.
        var wrongEventShape = CreateConformantLogoutToken();
        wrongEventShape.Events.Clear();
        wrongEventShape.Events["wrong-event"] = IdentityClaimValue.FromObject(Array.Empty<KeyValuePair<string, IdentityClaimValue>>());
        new OpenIdConnectLogoutToken(wrongEventShape).Validate(CreateOptions())
            .Errors.ShouldContain(d => d.Code == OpenIdConnectValidationCodes.LogoutEventInvalid);

        var stringPayload = CreateConformantLogoutToken();
        stringPayload.Events[OpenIdConnectEventTypes.BackChannelLogout] = "not-an-object";
        new OpenIdConnectLogoutToken(stringPayload).Validate(CreateOptions())
            .Errors.ShouldContain(d => d.Code == OpenIdConnectValidationCodes.LogoutEventInvalid);

        // A nonce is PROHIBITED on logout tokens.
        var withNonce = CreateConformantLogoutToken();
        withNonce.AdditionalClaims.Add(new IdentityClaim(OpenIdConnectClaimTypes.Nonce, "n-0S6"));
        new OpenIdConnectLogoutToken(withNonce).Validate(CreateOptions())
            .Errors.ShouldContain(d => d.Code == OpenIdConnectValidationCodes.LogoutTokenNonceProhibited);

        // Either sub or sid must identify what to log out.
        var anonymous = CreateConformantLogoutToken();
        anonymous.Subject = null;
        anonymous.SessionId = null;
        new OpenIdConnectLogoutToken(anonymous).Validate(CreateOptions())
            .Errors.ShouldContain(d => d.Code == OpenIdConnectValidationCodes.LogoutSubjectMissing);

        // Required claims are diagnostics, not guards.
        var missingJti = CreateConformantLogoutToken();
        missingJti.JwtId = null;
        new OpenIdConnectLogoutToken(missingJti).Validate(CreateOptions())
            .Errors.ShouldContain(d => d.Member == IdentityClaimTypes.JwtId);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Logout token extension claims must not collide with typed members")]
    public void LogoutToken_WhenExtensionClaimCollides_ShouldThrow()
    {
        // The single-source rule applies to all three token contracts; a colliding sub
        // extension claim could smuggle a subject past Validate().
        var colliding = CreateConformantLogoutToken();
        colliding.AdditionalClaims.Add(new IdentityClaim(IdentityClaimTypes.Subject, "someone-else"));

        Should.Throw<IdentityModelException>(() => new OpenIdConnectLogoutToken(colliding));

        // The prohibited nonce is deliberately NOT a collision — Validate() reports it.
        var withNonce = CreateConformantLogoutToken();
        withNonce.AdditionalClaims.Add(new IdentityClaim(OpenIdConnectClaimTypes.Nonce, "n-0S6"));
        Should.NotThrow(() => new OpenIdConnectLogoutToken(withNonce));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Back-channel logout should join onto stored sessions")]
    public void BackChannelLogout_WhenApplied_ShouldJoinOntoStoredSessions()
    {
        // Arrange — the session stored at login, with the subject derived through the
        // pinned wire-only recipe.
        var idToken = new OpenIdConnectIdToken(new OpenIdConnectIdTokenDescriptor
        {
            Issuer = "https://server.example.com",
            Subject = "24400320",
            ExpiresAt = now.AddMinutes(10),
            IssuedAt = now,
            SessionId = "08a5019c-17e1-4977-8f42-65a12843ea02",
        });

        var session = new AuthenticationSession(new AuthenticationSessionDescriptor
        {
            SessionId = "rp-session-1",
            Subject = idToken.GetSubjectIdentifier(),
            SubjectKind = IdentityKind.User,
            Protocol = AuthenticationProtocol.OpenIdConnect,
            Issuer = idToken.Issuer,
            CreatedAt = now,
            State = AuthenticationSessionState.Active,
            ProviderSessionIds = { idToken.SessionId! },
        });

        // Act — the logout token arrives later; Apply populates the shared members.
        var logoutToken = new OpenIdConnectLogoutToken(CreateConformantLogoutToken());
        var descriptor = new OpenIdConnectBackChannelLogoutRequestDescriptor();
        descriptor.Apply(logoutToken);
        var request = new OpenIdConnectBackChannelLogoutRequest(descriptor);

        // Assert — the (Issuer, ProviderSessionIds) join matches, and the subject derived
        // on the logout leg equals the one stored at login.
        request.Issuer.ShouldBe(session.Issuer);
        request.ProviderSessionIds.ShouldContain(session.ProviderSessionIds[0]);
        request.Subject.ShouldBe(session.Subject);
        request.Token.ShouldBeSameAs(logoutToken);
        request.LogoutToken.ShouldBe(logoutToken.RawToken);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Sub-only logout tokens should mean all sessions for the subject")]
    public void BackChannelLogout_WhenSidIsAbsent_ShouldMeanAllSessions()
    {
        // Arrange — a logout token identifying only the subject.
        var subOnly = CreateConformantLogoutToken();
        subOnly.SessionId = null;

        var descriptor = new OpenIdConnectBackChannelLogoutRequestDescriptor();
        descriptor.Apply(new OpenIdConnectLogoutToken(subOnly));

        // Act
        var request = new OpenIdConnectBackChannelLogoutRequest(descriptor);

        // Assert — the shared pin: empty session ids + a subject = every session.
        request.ProviderSessionIds.ShouldBeEmpty();
        request.Subject.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Back-channel materialization should reject token disagreement")]
    public void BackChannelLogout_WhenDescriptorDisagreesWithToken_ShouldThrow()
    {
        // Arrange — a descriptor populated by hand with a DIFFERENT subject than the token.
        var descriptor = new OpenIdConnectBackChannelLogoutRequestDescriptor
        {
            Token = new OpenIdConnectLogoutToken(CreateConformantLogoutToken()),
            Subject = new SubjectIdentifier("someone-else", issuer: "https://server.example.com"),
        };

        // Act + Assert — internal consistency between the two representations is
        // structural; Apply(token) is the supported population path.
        Should.Throw<IdentityModelException>(() => new OpenIdConnectBackChannelLogoutRequest(descriptor));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Back-channel materialization should reject issuer disagreement")]
    public void BackChannelLogout_WhenIssuerDisagreesWithToken_ShouldThrow()
    {
        // Arrange — the token names one issuer; the descriptor was populated (or left
        // unset) with another. The SLO join keys on Issuer, so this must be structural.
        var token = new OpenIdConnectLogoutToken(CreateConformantLogoutToken());
        var descriptor = new OpenIdConnectBackChannelLogoutRequestDescriptor();
        descriptor.Apply(token);
        descriptor.Issuer = "https://different-op.example";

        // Act + Assert
        Should.Throw<IdentityModelException>(() => new OpenIdConnectBackChannelLogoutRequest(descriptor));
    }
}
