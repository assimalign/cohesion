using System;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;
using Assimalign.Cohesion.IdentityModel.Tests.TestObjects;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Contains unit tests for the shared message envelope, including the pinned correlation
/// semantics.
/// </summary>
public sealed class ProtocolMessageTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Envelope should carry the shared members")]
    public void Constructor_WhenConstructed_ShouldCarrySharedMembers()
    {
        // Arrange — a SAML-shaped request envelope.
        var issuedAt = new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);
        var descriptor = new TestRequestDescriptor
        {
            MessageId = "_a41c9be2",
            Issuer = "https://sp.example",
            Destination = "https://idp.example/sso",
            IssuedAt = issuedAt,
            CorrelationState = "relay-42",
            RawMessage = "<samlp:AuthnRequest .../>",
        };

        // Act
        var message = new TestRequest(descriptor, AuthenticationProtocol.Saml2);

        // Assert
        message.Protocol.ShouldBe(AuthenticationProtocol.Saml2);
        message.MessageId.ShouldBe("_a41c9be2");
        message.Issuer.ShouldBe("https://sp.example");
        message.Destination.ShouldBe("https://idp.example/sso");
        message.IssuedAt.ShouldBe(issuedAt);
        message.CorrelationState.ShouldBe("relay-42");
        message.RawMessage.ShouldBe("<samlp:AuthnRequest .../>");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: OIDC-shaped messages should use CorrelationState, not InResponseTo")]
    public void CorrelationSemantics_WhenOidcShaped_ShouldUseCorrelationState()
    {
        // The pinned family rule: the OIDC `state` echo rides CorrelationState on BOTH
        // legs; InResponseTo is message-ID correlation and stays null for OIDC, which has
        // no message identifiers.
        var response = new TestResponse(new TestResponseDescriptor
        {
            Issuer = "https://op.example",
            CorrelationState = "af0ifjsldkj",
            Status = ProtocolResponseStatus.Success,
        }, AuthenticationProtocol.OpenIdConnect);

        response.CorrelationState.ShouldBe("af0ifjsldkj");
        response.InResponseTo.ShouldBeNull();
        response.MessageId.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Destination is wire capture and is not validated")]
    public void Destination_WhenMalformed_ShouldMaterialize()
    {
        // A malformed wire Destination is a validator finding, not a construction crash:
        // the envelope is descriptive wire capture.
        var descriptor = new TestRequestDescriptor { Destination = "::not a uri::" };

        var message = new TestRequest(descriptor, AuthenticationProtocol.Saml2);

        message.Destination.ShouldBe("::not a uri::");
    }
}
