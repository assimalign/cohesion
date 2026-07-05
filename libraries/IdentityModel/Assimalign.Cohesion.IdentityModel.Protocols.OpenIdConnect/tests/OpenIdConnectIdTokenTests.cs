using System;
using System.Linq;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;
using Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect.Tests;

/// <summary>
/// Contains unit tests and Core §3.1.3.7 data-rule fixtures for the ID token contract.
/// </summary>
public sealed class OpenIdConnectIdTokenTests
{
    private static readonly DateTimeOffset now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    private static OpenIdConnectIdTokenDescriptor CreateConformantDescriptor()
    {
        // Modeled on the OpenID Connect Core §A.2 example claim set.
        var descriptor = new OpenIdConnectIdTokenDescriptor
        {
            Issuer = "https://server.example.com",
            Subject = "24400320",
            ExpiresAt = now.AddMinutes(10),
            IssuedAt = now.AddMinutes(-1),
            AuthTime = now.AddMinutes(-2),
            Nonce = "n-0S6_WzA2Mj",
            JwtId = "jti-8842",
            RawToken = "eyJhbGciOiJSUzI1NiJ9.payload.signature",
        };
        descriptor.Audiences.Add("s6BhdRkqt3");
        descriptor.Amr.Add("pwd");
        return descriptor;
    }

    private static OpenIdConnectIdTokenValidationOptions CreateOptions()
        => new(validateAt: now)
        {
            ExpectedIssuer = "https://server.example.com",
            ExpectedAudience = "s6BhdRkqt3",
            ExpectedNonce = "n-0S6_WzA2Mj",
        };

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: The ID token claim collection is built from the typed members")]
    public void Claims_WhenMaterialized_ShouldBeBuiltFromTheTypedMembers()
    {
        // Arrange
        var descriptor = CreateConformantDescriptor();
        descriptor.AdditionalClaims.Add(new IdentityClaim("email", "user@example.com"));

        // Act
        var token = new OpenIdConnectIdToken(descriptor);

        // Assert — single source: typed members and the collection cannot disagree, wire
        // shapes are preserved (numeric dates as integers), and provenance is stamped.
        token.Claims.GetString(IdentityClaimTypes.Subject).ShouldBe("24400320");
        token.Claims.GetValues(IdentityClaimTypes.Audience).Count.ShouldBe(1);
        token.Claims.TryGet(IdentityClaimTypes.ExpirationTime, out var exp).ShouldBeTrue();
        exp.ShouldNotBeNull();
        exp.Value.Kind.ShouldBe(IdentityValueKind.Integer);
        exp.Value.AsInteger().ShouldBe(descriptor.ExpiresAt!.Value.ToUnixTimeSeconds());
        exp.Provenance!.Protocol.ShouldBe(AuthenticationProtocol.OpenIdConnect);
        token.Claims.GetString("email").ShouldBe("user@example.com");
        token.JwtId.ShouldBe("jti-8842"); // feeds AuthenticationResult.EvidenceId
        token.RawToken.ShouldNotBeNull(); // feeds the RP-initiated logout id_token_hint

        // A colliding extension claim would let Validate() pass claims F9 never checked.
        var colliding = CreateConformantDescriptor();
        colliding.AdditionalClaims.Add(new IdentityClaim(OpenIdConnectClaimTypes.Nonce, "other"));
        Should.Throw<IdentityModelException>(() => new OpenIdConnectIdToken(colliding));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: The subject identifier recipe is wire-only and symmetric")]
    public void GetSubjectIdentifier_WhenDerived_ShouldUseTheWireOnlyRecipe()
    {
        // Arrange
        var token = new OpenIdConnectIdToken(CreateConformantDescriptor());

        // Act
        var identifier = token.GetSubjectIdentifier();

        // Assert — Value=sub, Issuer=iss, unspecified format, no relying-party qualifier:
        // the same recipe the logout leg uses, so single-logout correlation cannot fork.
        identifier.ShouldNotBeNull();
        identifier!.Value.ShouldBe("24400320");
        identifier.Issuer.ShouldBe("https://server.example.com");
        identifier.Format.ShouldBe(SubjectIdentifierFormats.Unspecified);
        identifier.RelyingPartyQualifier.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: A conformant ID token should validate clean")]
    public void Validate_WhenConformant_ShouldSucceed()
    {
        var token = new OpenIdConnectIdToken(CreateConformantDescriptor());

        token.Validate(CreateOptions()).Succeeded.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Missing required claims are diagnostics, not guards")]
    public void Validate_WhenRequiredClaimsAreMissing_ShouldDiagnoseNotThrow()
    {
        // Arrange — the canonical negative fixture: an ID token missing exp and sub.
        var descriptor = CreateConformantDescriptor();
        descriptor.ExpiresAt = null;
        descriptor.Subject = null;

        // Act — materialization holds the evidence; validation produces the findings.
        var token = new OpenIdConnectIdToken(descriptor);
        var result = token.Validate(CreateOptions());

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.Member == IdentityClaimTypes.ExpirationTime);
        result.Errors.ShouldContain(d => d.Member == IdentityClaimTypes.Subject);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Audience rules should follow the Core severity map")]
    public void Validate_WhenAudiencesVary_ShouldFollowTheCoreSeverityMap()
    {
        // aud must contain the client (MUST -> Error).
        var wrongAudience = CreateConformantDescriptor();
        wrongAudience.Audiences.Clear();
        wrongAudience.Audiences.Add("other-client");
        new OpenIdConnectIdToken(wrongAudience).Validate(CreateOptions())
            .Errors.ShouldContain(d => d.Code == ProtocolValidationCodes.AudienceMismatch);

        // Additional audiences are untrusted BY DEFAULT (Core §3.1.3.7 step 3 posture).
        var multiAudience = CreateConformantDescriptor();
        multiAudience.Audiences.Add("resource-api");
        new OpenIdConnectIdToken(multiAudience).Validate(CreateOptions())
            .Errors.ShouldContain(d => d.Code == ProtocolValidationCodes.AudienceMismatch);

        // Trusting the additional audience clears the error, leaving only the azp SHOULD
        // warning for a multi-audience token.
        var options = CreateOptions();
        options.TrustedAudiences.Add("resource-api");
        var trusted = new OpenIdConnectIdToken(multiAudience).Validate(options);
        trusted.Succeeded.ShouldBeTrue();
        trusted.Diagnostics.ShouldContain(d =>
            d.Severity == ProtocolValidationSeverity.Warning && d.Code == OpenIdConnectValidationCodes.AzpInvalid);

        // The explicit opt-out also clears it.
        var optOut = CreateOptions();
        optOut.AllowAdditionalAudiences = true;
        new OpenIdConnectIdToken(multiAudience).Validate(optOut).Errors.ShouldBeEmpty();

        // Step 5 applies whenever azp is present, regardless of audience count.
        var singleAudienceWrongAzp = CreateConformantDescriptor();
        singleAudienceWrongAzp.Azp = "other-client";
        var azpFinding = new OpenIdConnectIdToken(singleAudienceWrongAzp).Validate(CreateOptions());
        azpFinding.Succeeded.ShouldBeTrue();
        azpFinding.Diagnostics.ShouldContain(d =>
            d.Severity == ProtocolValidationSeverity.Warning && d.Code == OpenIdConnectValidationCodes.AzpInvalid);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Temporal, nonce, and authentication-age rules should be enforced")]
    public void Validate_WhenTemporalOrNonceRulesFail_ShouldReport()
    {
        // Expired token.
        var expired = CreateConformantDescriptor();
        expired.ExpiresAt = now.AddHours(-1);
        new OpenIdConnectIdToken(expired).Validate(CreateOptions())
            .Errors.ShouldContain(d => d.Code == ProtocolValidationCodes.Expired);

        // Issued in the future.
        var future = CreateConformantDescriptor();
        future.IssuedAt = now.AddHours(1);
        new OpenIdConnectIdToken(future).Validate(CreateOptions())
            .Errors.ShouldContain(d => d.Code == ProtocolValidationCodes.NotYetValid);

        // Nonce mismatch.
        var replayed = CreateConformantDescriptor();
        replayed.Nonce = "different-nonce";
        new OpenIdConnectIdToken(replayed).Validate(CreateOptions())
            .Errors.ShouldContain(d => d.Code == OpenIdConnectValidationCodes.NonceMismatch);

        // max_age: auth_time becomes required and its age is enforced.
        var stale = CreateConformantDescriptor();
        stale.AuthTime = now.AddHours(-3);
        var maxAge = CreateOptions();
        maxAge.MaxAge = 3600;
        new OpenIdConnectIdToken(stale).Validate(maxAge)
            .Errors.ShouldContain(d => d.Code == OpenIdConnectValidationCodes.MaxAgeExceeded);

        var missingAuthTime = CreateConformantDescriptor();
        missingAuthTime.AuthTime = null;
        new OpenIdConnectIdToken(missingAuthTime).Validate(maxAge)
            .Errors.ShouldContain(d => d.Code == OpenIdConnectValidationCodes.AuthTimeMissing);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Unresolved claims sources are preserved outside the claim collection")]
    public void ClaimsSources_WhenPresent_ShouldBePreservedOutsideTheClaims()
    {
        // Arrange — an Azure-AD-style groups overage: distributed claims reference.
        var descriptor = CreateConformantDescriptor();
        descriptor.ClaimsSources.Add(new OpenIdConnectClaimsSource(
            sourceId: "src1",
            claimNames: ["groups"],
            endpoint: "https://graph.example/users/24400320/groups",
            accessToken: "ksj3n283dke"));

        // Act
        var token = new OpenIdConnectIdToken(descriptor);

        // Assert — the reference survives; the canonical collection never carries it.
        token.ClaimsSources.Count.ShouldBe(1);
        token.ClaimsSources[0].ClaimNames.ShouldBe(["groups"]);
        token.ClaimsSources[0].Endpoint.ShouldNotBeNull();
        token.Claims.Contains("groups").ShouldBeFalse();
        token.Claims.Contains("_claim_sources").ShouldBeFalse();
    }
}
