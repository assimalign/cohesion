using System;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;
using Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect.Tests;

/// <summary>
/// Contains unit tests for the token endpoint request and response contracts.
/// </summary>
public sealed class OpenIdConnectTokenMessageTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Token requests should carry the code exchange and client assertions")]
    public void TokenRequest_WhenConstructed_ShouldCarryExchangeAndClientAssertion()
    {
        // The grant type is the structural guard.
        Should.Throw<IdentityModelException>(() => new OpenIdConnectTokenRequest(
            new OpenIdConnectTokenRequestDescriptor { ClientId = "client-1" }));

        // A private_key_jwt code exchange: the assertion is a signed public artifact —
        // representable — while client secrets never are.
        var request = new OpenIdConnectTokenRequest(new OpenIdConnectTokenRequestDescriptor
        {
            GrantType = OpenIdConnectGrantTypes.AuthorizationCode,
            ClientId = "s6BhdRkqt3",
            Code = "SplxlOBeZQQYbYS6WxSbIA",
            RedirectUri = "https://rp.example/cb",
            CodeVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk",
            ClientAssertion = "eyJhbGciOiJFUzI1NiJ9.payload.signature",
            ClientAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
        });

        request.GrantType.ShouldBe("authorization_code");
        request.ClientId.ShouldBe("s6BhdRkqt3");
        request.CodeVerifier.ShouldNotBeNull();
        request.ClientAssertionType.ShouldBe("urn:ietf:params:oauth:client-assertion-type:jwt-bearer");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Token responses should hold non-conformant documents and diagnose them")]
    public void TokenResponse_WhenNonConformant_ShouldMaterializeAndDiagnose()
    {
        // Arrange — the single most common real-world non-conformance: no token_type.
        var missingTokenType = new OpenIdConnectTokenResponse(new OpenIdConnectTokenResponseDescriptor
        {
            AccessToken = "SlAV32hkKG",
            ExpiresIn = 3600,
            IdToken = "eyJhbGciOiJSUzI1NiJ9.payload.signature",
            Status = ProtocolResponseStatus.Success,
        });

        // Act
        var result = missingTokenType.Validate(expectIdToken: true);

        // Assert — materialization held the document; validation produced the diagnostic.
        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.Code == OpenIdConnectValidationCodes.MissingTokenType);

        // A conformant response validates clean, including the ID token expectation.
        var conformant = new OpenIdConnectTokenResponse(new OpenIdConnectTokenResponseDescriptor
        {
            AccessToken = "SlAV32hkKG",
            TokenType = "Bearer",
            ExpiresIn = 3600,
            RefreshToken = "8xLOxBtZp8",
            IdToken = "eyJhbGciOiJSUzI1NiJ9.payload.signature",
            Status = ProtocolResponseStatus.Success,
        });

        conformant.Validate(expectIdToken: true).Succeeded.ShouldBeTrue();

        // An OIDC exchange without an ID token is an error; a pure-OAuth one is not.
        var noIdToken = new OpenIdConnectTokenResponse(new OpenIdConnectTokenResponseDescriptor
        {
            AccessToken = "SlAV32hkKG",
            TokenType = "Bearer",
            Status = ProtocolResponseStatus.Success,
        });

        noIdToken.Validate(expectIdToken: true).Errors.ShouldContain(d => d.Member == "id_token");
        noIdToken.Validate(expectIdToken: false).Succeeded.ShouldBeTrue();

        // Error responses skip the success rules entirely.
        var error = new OpenIdConnectTokenResponse(new OpenIdConnectTokenResponseDescriptor
        {
            Status = ProtocolResponseStatus.Failed(OpenIdConnectErrorCodes.InvalidGrant, "The code is expired."),
        });

        error.Validate(expectIdToken: true).Succeeded.ShouldBeTrue();
        error.Status.Code.ShouldBe("invalid_grant");
    }
}
