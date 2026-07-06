using System;
using System.Buffers.Text;
using System.Text;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken.Tests;

/// <summary>
/// Contains unit tests and specification fixtures for the JSON Web Token document model:
/// compact parsing, header and claim capture, the computed OIDC accessors, and the
/// document-level validation rules.
/// </summary>
public sealed class JsonWebTokenTests
{
    private static readonly DateTimeOffset now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    // OpenID Connect Core §3.1.3.6 worked at_hash example: RS256 over this access token.
    private const string SpecAccessToken = "jHkWEdUXMU1BwAsC4vtUsZwnNvTIxEl0z9K3vx5KF0Y";
    private const string SpecAccessTokenHash = "77QmUPtjPfzWtF2AnpK9RQ";

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Parse: A well-formed token exposes header, claims, and temporals")]
    public void Parse_WhenWellFormed_ShouldExposeHeaderClaimsAndTemporals()
    {
        var token = JsonWebToken.Parse(ConformantCompact());

        token.Kind.ShouldBe(IdentityTokenKind.JsonWebToken);
        token.Algorithm.ShouldBe(JoseAlgorithms.RS256);
        token.Header.Type.ShouldBe("JWT");
        token.Header.KeyId.ShouldBe("key-1");
        token.TokenType.ShouldBe("JWT");
        token.Issuer.ShouldBe("https://issuer.example.com");
        token.Subject.ShouldNotBeNull();
        token.Subject.Value.ShouldBe("user-42");
        token.Subject.Issuer.ShouldBe("https://issuer.example.com");
        token.Audiences.ShouldContain("client-1");
        token.ExpiresAt.ShouldBe(now.AddHours(1));
        token.IssuedAt.ShouldBe(now);
        token.Nonce.ShouldBe("n-abc");
        token.AuthorizedParty.ShouldBe("client-1");
        token.SessionId.ShouldBe("sess-1");
        token.AuthTime.ShouldBe(now.AddMinutes(-5));
        token.SigningInput.ShouldNotBeNull();
        // jti projects onto the neutral Id (feeds AuthenticationResult evidence linkage).
        token.Id.ShouldBe("id-123");
        token.NotBefore.ShouldBe(now.AddMinutes(-5));
        token.AuthenticationContextClassReference.ShouldBe("urn:mace:incommon:iap:silver");
        token.AuthenticationMethodReferences.ShouldBe(new[] { "pwd", "otp" });
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Parse: A single-string aud maps to one audience and one claim")]
    public void Parse_WhenAudienceIsString_ShouldMapConsistently()
    {
        var token = JsonWebToken.Parse(Compact(
            """{"alg":"RS256"}""",
            """{"iss":"i","sub":"s","aud":"client-1"}"""));

        token.Audiences.ShouldBe(new[] { "client-1" });
        token.Claims.GetValues(IdentityClaimTypes.Audience).Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Parse: A multi-value aud folds into audiences and duplicate claims")]
    public void Parse_WhenAudienceIsArray_ShouldFoldConsistently()
    {
        var token = JsonWebToken.Parse(Compact(
            """{"alg":"RS256"}""",
            """{"iss":"i","sub":"s","aud":["a1","a2","a3"]}"""));

        token.Audiences.ShouldBe(new[] { "a1", "a2", "a3" });
        // The typed audience list and the claim record must agree (anti-drift).
        token.Claims.GetValues(IdentityClaimTypes.Audience).Count.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Parse: An extreme exp does not throw and leaves the projection null")]
    public void Parse_WhenExpirationOutOfRange_ShouldNotThrow()
    {
        var token = JsonWebToken.Parse(Compact(
            """{"alg":"RS256"}""",
            """{"iss":"i","sub":"s","exp":1e300}"""));

        // The raw claim survives; the bounded DateTimeOffset projection degrades to null.
        token.ExpiresAt.ShouldBeNull();
        token.Claims.Contains(IdentityClaimTypes.ExpirationTime).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - TryParse: Malformed tokens are rejected")]
    [InlineData("only.two")]
    [InlineData("!!!.eyJhIjoxfQ.sig")]
    [InlineData("")]
    public void TryParse_WhenMalformed_ShouldReturnFalse(string token)
    {
        JsonWebToken.TryParse(token, out var parsed).ShouldBeFalse();
        parsed.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - TryParse: A non-object payload is rejected")]
    public void TryParse_WhenPayloadNotObject_ShouldReturnFalse()
    {
        JsonWebToken.TryParse(Compact("""{"alg":"RS256"}""", "[1,2,3]"), out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - TryParse: A duplicate member is rejected (RFC 8725)")]
    public void TryParse_WhenDuplicateMember_ShouldReturnFalse()
    {
        JsonWebToken.TryParse(Compact("""{"alg":"RS256"}""", """{"sub":"a","sub":"b"}"""), out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - TryParse: An over-deep claim graph is rejected")]
    public void TryParse_WhenClaimGraphTooDeep_ShouldReturnFalse()
    {
        var deep = new StringBuilder();
        for (var i = 0; i < 80; i++)
        {
            deep.Append("""{"a":""");
        }

        deep.Append('1');
        for (var i = 0; i < 80; i++)
        {
            deep.Append('}');
        }

        JsonWebToken.TryParse(Compact("""{"alg":"RS256"}""", $$"""{"nested":{{deep}}}"""), out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Validate: A conformant token validates")]
    public void Validate_WhenConformant_ShouldSucceed()
    {
        var token = JsonWebToken.Parse(ConformantCompact());

        var result = token.Validate(new JsonWebTokenValidationOptions(now)
        {
            ExpectedIssuer = "https://issuer.example.com",
            ExpectedAudience = "client-1",
        });

        result.Succeeded.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Validate: The 'none' algorithm is rejected by default")]
    public void Validate_WhenUnsecured_ShouldReportAlgorithmNone()
    {
        var token = JsonWebToken.Parse(Compact("""{"alg":"none"}""", """{"iss":"i","sub":"s"}"""));

        var result = token.Validate(new JsonWebTokenValidationOptions(now));

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == JsonWebTokenValidationCodes.AlgorithmNone);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Validate: The 'none' algorithm is accepted when explicitly allowed")]
    public void Validate_WhenUnsecuredAllowed_ShouldSucceed()
    {
        var token = JsonWebToken.Parse(Compact("""{"alg":"none"}""", """{"iss":"i","sub":"s"}"""));

        token.Validate(new JsonWebTokenValidationOptions(now) { AllowUnsecured = true }).Succeeded.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Validate: An algorithm outside the allowed set fails")]
    public void Validate_WhenAlgorithmNotAllowed_ShouldReportUnsupportedAlgorithm()
    {
        var token = JsonWebToken.Parse(Compact("""{"alg":"HS256"}""", """{"iss":"i","sub":"s"}"""));

        var options = new JsonWebTokenValidationOptions(now);
        options.AllowedAlgorithms.Add(JoseAlgorithms.RS256);

        var result = token.Validate(options);

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == JsonWebTokenValidationCodes.UnsupportedAlgorithm);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Validate: A missing required claim fails")]
    public void Validate_WhenRequiredClaimMissing_ShouldReportMissingRequiredMember()
    {
        var token = JsonWebToken.Parse(Compact("""{"alg":"RS256"}""", """{"iss":"i"}"""));

        var options = new JsonWebTokenValidationOptions(now);
        options.RequiredClaims.Add(IdentityClaimTypes.Subject);

        var result = token.Validate(options);

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == TokenValidationCodes.MissingRequiredMember);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Validate: An unencoded payload (b64:false) is rejected")]
    public void Validate_WhenUnencodedPayload_ShouldReportUnsupportedBase64Payload()
    {
        var token = JsonWebToken.Parse(Compact("""{"alg":"RS256","b64":false}""", """{"iss":"i","sub":"s"}"""));

        var result = token.Validate(new JsonWebTokenValidationOptions(now));

        result.Errors.ShouldContain(e => e.Code == JsonWebTokenValidationCodes.UnsupportedBase64Payload);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Validate: An unrecognized critical header is rejected")]
    public void Validate_WhenUnknownCriticalHeader_ShouldReportUnrecognizedCriticalHeader()
    {
        var token = JsonWebToken.Parse(Compact("""{"alg":"RS256","crit":["ext1"],"ext1":true}""", """{"iss":"i","sub":"s"}"""));

        var result = token.Validate(new JsonWebTokenValidationOptions(now));

        result.Errors.ShouldContain(e => e.Code == JsonWebTokenValidationCodes.UnrecognizedCriticalHeader);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Validate: An expired token fails via the base rules")]
    public void Validate_WhenExpired_ShouldReportExpired()
    {
        var token = JsonWebToken.Parse(ConformantCompact());

        var result = token.Validate(new JsonWebTokenValidationOptions(now.AddHours(2)));

        result.Errors.ShouldContain(e => e.Code == TokenValidationCodes.Expired);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Validate: A matching at_hash validates")]
    public void Validate_WhenAtHashMatches_ShouldSucceed()
    {
        var token = JsonWebToken.Parse(Compact(
            """{"alg":"RS256"}""",
            $$"""{"iss":"i","sub":"s","at_hash":"{{SpecAccessTokenHash}}"}"""));

        var result = token.Validate(new JsonWebTokenValidationOptions(now) { AccessToken = SpecAccessToken });

        result.Succeeded.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Validate: A mismatched at_hash fails")]
    public void Validate_WhenAtHashMismatched_ShouldReportMismatch()
    {
        var token = JsonWebToken.Parse(Compact(
            """{"alg":"RS256"}""",
            $$"""{"iss":"i","sub":"s","at_hash":"{{SpecAccessTokenHash}}"}"""));

        var result = token.Validate(new JsonWebTokenValidationOptions(now) { AccessToken = "a-different-access-token" });

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == JsonWebTokenValidationCodes.AccessTokenHashMismatch);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Validate: A missing at_hash is an error only when required")]
    public void Validate_WhenAtHashMissing_ShouldHonorRequireFlag()
    {
        var token = JsonWebToken.Parse(Compact("""{"alg":"RS256"}""", """{"iss":"i","sub":"s"}"""));

        // Default: a missing hash claim is a warning (non-fatal) — pure code-flow tokens stay valid.
        token.Validate(new JsonWebTokenValidationOptions(now) { AccessToken = SpecAccessToken })
            .Succeeded.ShouldBeTrue();

        // RequireTokenHash escalates it to an error.
        var required = token.Validate(new JsonWebTokenValidationOptions(now)
        {
            AccessToken = SpecAccessToken,
            RequireTokenHash = true,
        });
        required.Succeeded.ShouldBeFalse();
        required.Errors.ShouldContain(e => e.Code == JsonWebTokenValidationCodes.TokenHashMissing);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Validate: A mismatched c_hash fails")]
    public void Validate_WhenCodeHashMismatched_ShouldReportMismatch()
    {
        // c_hash uses the same half-SHA-2 as at_hash; reuse the spec vector for the code hash.
        var token = JsonWebToken.Parse(Compact(
            """{"alg":"RS256"}""",
            $$"""{"iss":"i","sub":"s","c_hash":"{{SpecAccessTokenHash}}"}"""));

        // A matching code validates; a different code is a mismatch.
        token.Validate(new JsonWebTokenValidationOptions(now) { AuthorizationCode = SpecAccessToken })
            .Succeeded.ShouldBeTrue();

        var result = token.Validate(new JsonWebTokenValidationOptions(now) { AuthorizationCode = "a-different-code" });
        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == JsonWebTokenValidationCodes.CodeHashMismatch);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Validate: A hash claim with an algorithm that has no defined hash fails")]
    public void Validate_WhenHashAlgorithmUndefined_ShouldReportUnsupportedAlgorithm()
    {
        // alg=none has no defined at_hash pairing; a present at_hash cannot be verified.
        var token = JsonWebToken.Parse(Compact(
            """{"alg":"none"}""",
            $$"""{"iss":"i","sub":"s","at_hash":"{{SpecAccessTokenHash}}"}"""));

        var result = token.Validate(new JsonWebTokenValidationOptions(now)
        {
            AllowUnsecured = true, // isolate the hash path from the none-rejection rule.
            AccessToken = SpecAccessToken,
        });

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == JsonWebTokenValidationCodes.UnsupportedAlgorithm);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - TokenHashComputer: Matches the OIDC Core at_hash example")]
    public void TokenHashComputer_WhenGivenSpecVector_ShouldMatch()
    {
        TokenHashComputer.ComputeHalfHash(JoseAlgorithms.RS256, SpecAccessToken).ShouldBe(SpecAccessTokenHash);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - JsonWebTokenParts: Parses three segments")]
    public void JsonWebTokenParts_TryParse_WhenThreeSegments_ShouldSucceed()
    {
        JsonWebTokenParts.TryParse("header.payload.", out var parts).ShouldBeTrue();
        parts.ShouldNotBeNull();
        parts.SigningInput.ShouldBe("header.payload");
    }

    private static string ConformantCompact()
    {
        var exp = now.AddHours(1).ToUnixTimeSeconds();
        var iat = now.ToUnixTimeSeconds();
        var nbf = now.AddMinutes(-5).ToUnixTimeSeconds();
        var authTime = now.AddMinutes(-5).ToUnixTimeSeconds();

        var header = """{"alg":"RS256","typ":"JWT","kid":"key-1"}""";
        var payload =
            $$"""
            {"iss":"https://issuer.example.com","sub":"user-42","aud":"client-1","jti":"id-123","exp":{{exp}},"iat":{{iat}},"nbf":{{nbf}},"nonce":"n-abc","azp":"client-1","auth_time":{{authTime}},"sid":"sess-1","acr":"urn:mace:incommon:iap:silver","amr":["pwd","otp"]}
            """;
        return Compact(header, payload);
    }

    private static string Compact(string headerJson, string payloadJson, string signature = "c2ln")
        => $"{Base64UrlJson(headerJson)}.{Base64UrlJson(payloadJson)}.{signature}";

    private static string Base64UrlJson(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
}
