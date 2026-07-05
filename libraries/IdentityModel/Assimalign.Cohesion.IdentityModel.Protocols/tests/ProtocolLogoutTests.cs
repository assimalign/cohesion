using System;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;
using Assimalign.Cohesion.IdentityModel.Protocols.Tests.TestObjects;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Tests;

/// <summary>
/// Contains unit tests for the shared logout semantics, including the correlation join
/// with the authentication session model.
/// </summary>
public sealed class ProtocolLogoutTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Logout requests should correlate onto stored sessions")]
    public void LogoutRequest_WhenReceived_ShouldCorrelateOntoStoredSessions()
    {
        // Arrange — a stored session and an inbound SAML logout request from the same
        // provider citing one of its session indexes: the (Issuer, ProviderSessionIds)
        // pair is the single-logout join key on both sides.
        var session = new AuthenticationSession(new AuthenticationSessionDescriptor
        {
            SessionId = "sp-session-1",
            Subject = new SubjectIdentifier("x9f2", SubjectIdentifierFormats.Persistent, "https://idp.example"),
            SubjectKind = IdentityKind.User,
            Protocol = AuthenticationProtocol.Saml2,
            Issuer = "https://idp.example",
            CreatedAt = new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero),
            State = AuthenticationSessionState.Active,
            ProviderSessionIds = { "index-a", "index-b" },
        });

        var logoutDescriptor = new TestLogoutRequestDescriptor
        {
            MessageId = "_logout-1",
            Issuer = "https://idp.example",
            Subject = new SubjectIdentifier("x9f2", SubjectIdentifierFormats.Persistent, "https://idp.example"),
        };
        logoutDescriptor.ProviderSessionIds.Add("index-b");

        // Act
        var logout = new TestLogoutRequest(logoutDescriptor, AuthenticationProtocol.Saml2);

        // Assert — orchestration can match without any protocol-specific knowledge.
        var issuerMatches = string.Equals(logout.Issuer, session.Issuer, StringComparison.Ordinal);
        var sessionMatches = false;
        foreach (var candidate in logout.ProviderSessionIds)
        {
            foreach (var stored in session.ProviderSessionIds)
            {
                sessionMatches |= string.Equals(candidate, stored, StringComparison.Ordinal);
            }
        }

        issuerMatches.ShouldBeTrue();
        sessionMatches.ShouldBeTrue();
        logout.Subject.ShouldBe(session.Subject);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Logout with no session ids should mean all sessions")]
    public void LogoutRequest_WhenNoSessionIdsAreCited_ShouldMeanAllSessionsForTheSubject()
    {
        // SAML semantics: a LogoutRequest without SessionIndex applies to every session
        // of the named principal.
        var descriptor = new TestLogoutRequestDescriptor
        {
            Issuer = "https://idp.example",
            Subject = new SubjectIdentifier("user-1", issuer: "https://idp.example"),
            Reason = "urn:oasis:names:tc:SAML:2.0:logout:admin",
            NotOnOrAfter = new DateTimeOffset(2026, 7, 3, 12, 5, 0, TimeSpan.Zero),
        };

        var logout = new TestLogoutRequest(descriptor, AuthenticationProtocol.Saml2);

        logout.ProviderSessionIds.ShouldBeEmpty();
        logout.Subject.ShouldNotBeNull();
        logout.Reason.ShouldBe("urn:oasis:names:tc:SAML:2.0:logout:admin");
        logout.NotOnOrAfter.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Logout responses should carry partial outcomes")]
    public void LogoutResponse_WhenPartial_ShouldCarryThePartialOutcome()
    {
        var logout = new TestLogoutRequest(new TestLogoutRequestDescriptor(), AuthenticationProtocol.Saml2);

        logout.Subject.ShouldBeNull(); // an EncryptedID cannot be resolved by a descriptive library

        // The partial outcome flows through the response envelope's inherited Status.
        var response = new TestLogoutResponse(new TestLogoutResponseDescriptor
        {
            Issuer = "https://sp.example",
            InResponseTo = "_logout-1",
            Status = ProtocolResponseStatus.SuccessWith(
                code: "urn:oasis:names:tc:SAML:2.0:status:Success",
                subCodes: ["urn:oasis:names:tc:SAML:2.0:status:PartialLogout"]),
        }, AuthenticationProtocol.Saml2);

        response.Status.Succeeded.ShouldBeTrue();
        response.Status.SubCodes.ShouldContain("urn:oasis:names:tc:SAML:2.0:status:PartialLogout");
        response.InResponseTo.ShouldBe("_logout-1");

        // The fail-closed status rule applies to logout responses like any response.
        Should.Throw<IdentityModelException>(() =>
            new TestLogoutResponse(new TestLogoutResponseDescriptor(), AuthenticationProtocol.Saml2));
    }
}
