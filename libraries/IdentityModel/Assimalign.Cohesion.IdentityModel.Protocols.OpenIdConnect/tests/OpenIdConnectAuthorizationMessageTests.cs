using System;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;
using Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect.Tests;

/// <summary>
/// Contains unit tests for the authorization request and response contracts.
/// </summary>
public sealed class OpenIdConnectAuthorizationMessageTests
{
    private static OpenIdConnectAuthorizationRequestDescriptor CreateCodeRequest()
    {
        var descriptor = new OpenIdConnectAuthorizationRequestDescriptor
        {
            ClientId = "s6BhdRkqt3",
            ResponseType = OpenIdConnectResponseTypes.Code,
            RedirectUri = "https://rp.example/cb",
            Nonce = "n-0S6_WzA2Mj",
            CodeChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            CodeChallengeMethod = "S256",
            CorrelationState = "af0ifjsldkj",
        };
        descriptor.Scopes.Add(OpenIdConnectScopes.OpenId);
        descriptor.Scopes.Add(OpenIdConnectScopes.Profile);
        return descriptor;
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Authorization requests should guard identity and diagnose conformance")]
    public void AuthorizationRequest_WhenConstructedAndValidated_ShouldSplitGuardsFromDiagnostics()
    {
        // The identity members are structural guards.
        Should.Throw<IdentityModelException>(() => new OpenIdConnectAuthorizationRequest(
            new OpenIdConnectAuthorizationRequestDescriptor { ResponseType = "code" }));
        Should.Throw<IdentityModelException>(() => new OpenIdConnectAuthorizationRequest(
            new OpenIdConnectAuthorizationRequestDescriptor { ClientId = "client-1" }));

        // A conformant PKCE code request validates clean; ClientId aliases the envelope sender.
        var request = new OpenIdConnectAuthorizationRequest(CreateCodeRequest());
        request.ClientId.ShouldBe("s6BhdRkqt3");
        request.Issuer.ShouldBe("s6BhdRkqt3");
        request.CorrelationState.ShouldBe("af0ifjsldkj");
        request.Validate().Succeeded.ShouldBeTrue();

        // Conformance failures are diagnostics, not guards.
        var missingOpenId = CreateCodeRequest();
        missingOpenId.Scopes.Clear();
        new OpenIdConnectAuthorizationRequest(missingOpenId).Validate()
            .Errors.ShouldContain(d => d.Code == OpenIdConnectValidationCodes.MissingOpenIdScope);

        var noPkce = CreateCodeRequest();
        noPkce.CodeChallenge = null;
        var pkceFinding = new OpenIdConnectAuthorizationRequest(noPkce).Validate();
        pkceFinding.Succeeded.ShouldBeTrue(); // best-current-practice warning, not an error
        pkceFinding.Diagnostics.ShouldContain(d =>
            d.Severity == ProtocolValidationSeverity.Warning && d.Code == OpenIdConnectValidationCodes.PkceMissing);

        var hybridWithoutNonce = CreateCodeRequest();
        hybridWithoutNonce.ResponseType = "code id_token";
        hybridWithoutNonce.Nonce = null;
        new OpenIdConnectAuthorizationRequest(hybridWithoutNonce).Validate()
            .Errors.ShouldContain(d => d.Code == OpenIdConnectValidationCodes.NonceMissing);

        var promptNoneCombined = CreateCodeRequest();
        promptNoneCombined.Prompts.Add(OpenIdConnectPromptValues.None);
        promptNoneCombined.Prompts.Add(OpenIdConnectPromptValues.Login);
        new OpenIdConnectAuthorizationRequest(promptNoneCombined).Validate()
            .Errors.ShouldContain(d => d.Code == OpenIdConnectValidationCodes.PromptNoneCombined);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Response type comparison should be order-insensitive")]
    public void ResponseTypes_WhenCompared_ShouldBeOrderInsensitive()
    {
        // OAuth Multiple Response Types §3: the value is a space-delimited unordered set.
        OpenIdConnectResponseTypes.Matches("code id_token", "id_token code").ShouldBeTrue();
        OpenIdConnectResponseTypes.Matches("code", "code").ShouldBeTrue();
        OpenIdConnectResponseTypes.Matches("code id_token", "code").ShouldBeFalse();
        OpenIdConnectResponseTypes.Matches(null, null).ShouldBeTrue();
        OpenIdConnectResponseTypes.Matches("code", null).ShouldBeFalse();

        // Set semantics: duplicates collapse and the relation is symmetric.
        OpenIdConnectResponseTypes.Matches("code code", "code id_token").ShouldBeFalse();
        OpenIdConnectResponseTypes.Matches("code id_token", "code code").ShouldBeFalse();
        OpenIdConnectResponseTypes.Matches("code code", "code").ShouldBeTrue();

        var hybrid = CreateCodeRequest();
        hybrid.ResponseType = "id_token code";
        var request = new OpenIdConnectAuthorizationRequest(hybrid);

        request.ResponseTypeParts.ShouldBe(["id_token", "code"]);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Authorization responses should validate against the requesting context")]
    public void AuthorizationResponse_WhenValidated_ShouldCheckStateIssuerAndArtifacts()
    {
        // Arrange — a code response carrying the RFC 9207 iss parameter.
        var response = new OpenIdConnectAuthorizationResponse(new OpenIdConnectAuthorizationResponseDescriptor
        {
            Code = "SplxlOBeZQQYbYS6WxSbIA",
            CorrelationState = "af0ifjsldkj",
            Issuer = "https://op.example",
            Status = ProtocolResponseStatus.Success,
        });

        var clean = response.Validate(new OpenIdConnectAuthorizationResponseValidationOptions
        {
            ExpectedCorrelationState = "af0ifjsldkj",
            ExpectedIssuer = "https://op.example",
            ExpectedResponseType = OpenIdConnectResponseTypes.Code,
        });
        clean.Succeeded.ShouldBeTrue();

        // A tampered state echo and a wrong issuer are errors.
        var tampered = response.Validate(new OpenIdConnectAuthorizationResponseValidationOptions
        {
            ExpectedCorrelationState = "different",
            ExpectedIssuer = "https://attacker.example",
        });
        tampered.Errors.ShouldContain(d => d.Code == ProtocolValidationCodes.CorrelationMismatch);
        tampered.Errors.ShouldContain(d => d.Code == ProtocolValidationCodes.IssuerMismatch);

        // A success response missing the promised artifact is a diagnostic, not a guard —
        // and response_type=none legally succeeds with no artifact at all.
        var artifactless = new OpenIdConnectAuthorizationResponse(new OpenIdConnectAuthorizationResponseDescriptor
        {
            Status = ProtocolResponseStatus.Success,
        });

        artifactless.Validate(new OpenIdConnectAuthorizationResponseValidationOptions
        {
            ExpectedResponseType = OpenIdConnectResponseTypes.Code,
        }).Errors.ShouldContain(d => d.Code == OpenIdConnectValidationCodes.MissingSuccessArtifact);

        artifactless.Validate(new OpenIdConnectAuthorizationResponseValidationOptions
        {
            ExpectedResponseType = OpenIdConnectResponseTypes.None,
        }).Succeeded.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Error responses should map the wire error onto the status")]
    public void AuthorizationResponse_WhenErrorShaped_ShouldCarryTheWireError()
    {
        // Arrange — Core §3.1.2.6 error semantics through the shared status.
        var response = new OpenIdConnectAuthorizationResponse(new OpenIdConnectAuthorizationResponseDescriptor
        {
            CorrelationState = "af0ifjsldkj",
            Status = ProtocolResponseStatus.Failed(
                OpenIdConnectErrorCodes.LoginRequired,
                message: "End-user authentication is required."),
        });

        // Assert
        response.Status.Succeeded.ShouldBeFalse();
        response.Status.Code.ShouldBe("login_required");

        // RFC 9207 §2.4: an absent iss from a provider that advertises support MUST be
        // rejected — this is the mix-up attack window.
        var absent = response.Validate(new OpenIdConnectAuthorizationResponseValidationOptions
        {
            ExpectedIssuer = "https://op.example",
            IssuerParameterAdvertised = true,
        });
        absent.Succeeded.ShouldBeFalse();
        absent.Errors.ShouldContain(d => d.Code == OpenIdConnectValidationCodes.IssParameterMissing);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Response modes should map onto the transport-shaped bindings")]
    public void ResponseModes_WhenMapped_ShouldYieldTheSharedBindings()
    {
        OpenIdConnectResponseModes.GetBinding("query").ShouldBe(ProtocolBinding.HttpRedirect);
        OpenIdConnectResponseModes.GetBinding("fragment").ShouldBe(ProtocolBinding.HttpFragment);
        OpenIdConnectResponseModes.GetBinding("form_post").ShouldBe(ProtocolBinding.HttpPost);
        OpenIdConnectResponseModes.GetBinding("jwt").ShouldBe(ProtocolBinding.Unknown);
    }
}
