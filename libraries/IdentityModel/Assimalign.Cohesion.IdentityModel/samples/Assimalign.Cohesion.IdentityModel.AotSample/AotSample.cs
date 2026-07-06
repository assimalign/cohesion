using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Text;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;
using Assimalign.Cohesion.IdentityModel.Token;

using ProtocolSaml = Assimalign.Cohesion.IdentityModel.Protocols.Saml;
using TokenJwt = Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;
using TokenSaml = Assimalign.Cohesion.IdentityModel.Token.Saml;

namespace Assimalign.Cohesion.IdentityModel.AotSample;

/// <summary>
/// Exercises the representative surfaces of every IdentityModel family assembly under
/// NativeAOT: the typed claim-value model (the family's trim-safe substitute for reflection),
/// the JWT compact-parse path (the family's only wire-format parser — reflection-free
/// <c>System.Text.Json</c> readers plus <c>Base64Url</c> and one-shot SHA-2 hashing), both
/// protocol contract branches, both token documents, and the cross-protocol canonicalization
/// seam. Deterministic output; a non-zero exit means a check failed. SAML XML parsing is out
/// of scope because no XML parser exists in the family yet — when one lands, this smoke
/// carries the obligation to exercise it.
/// </summary>
internal static class AotSample
{
    private static readonly DateTimeOffset Now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    // OpenID Connect Core §3.1.3.6 worked at_hash example (RS256).
    private const string SpecAccessToken = "jHkWEdUXMU1BwAsC4vtUsZwnNvTIxEl0z9K3vx5KF0Y";
    private const string SpecAccessTokenHash = "77QmUPtjPfzWtF2AnpK9RQ";

    private static int Main()
    {
        try
        {
            CheckClaimValueKinds();
            CheckJwtParseAndValidate();
            CheckSamlTokenValidate();
            CheckCrossProtocolCanonicalization();

            Console.WriteLine("PASS: IdentityModel NativeAOT smoke completed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"FAIL: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static void CheckClaimValueKinds()
    {
        // Every value kind through the typed union — the trim-safe substitute for reflection.
        var values = new[]
        {
            IdentityClaimValue.Null,
            IdentityClaimValue.FromString("text"),
            IdentityClaimValue.FromBoolean(true),
            IdentityClaimValue.FromInteger(42),
            IdentityClaimValue.FromDouble(4.2),
            IdentityClaimValue.FromDecimal(4.2m),
            IdentityClaimValue.FromDateTime(Now),
            IdentityClaimValue.FromBinary(new byte[] { 1, 2, 3 }),
            IdentityClaimValue.FromArray(new[] { IdentityClaimValue.FromString("a") }),
            IdentityClaimValue.FromObject(new[]
            {
                new KeyValuePair<string, IdentityClaimValue>("k", IdentityClaimValue.FromString("v")),
            }),
        };

        var kinds = new HashSet<IdentityValueKind>();
        foreach (var value in values)
        {
            kinds.Add(value.Kind);
        }

        Require(kinds.Count == values.Length, "every claim-value kind materializes distinctly");
        Console.WriteLine($"OK: claim-value kinds ({kinds.Count})");
    }

    private static void CheckJwtParseAndValidate()
    {
        // The family's only wire-format parse path: base64url + Utf8 JSON readers, then the
        // keyless at_hash comparison (SHA-256 under ILC) via the spec vector.
        var exp = Now.AddHours(1).ToUnixTimeSeconds();
        var header = """{"alg":"RS256","typ":"JWT"}""";
        var payload =
            $$"""{"iss":"https://op.example.com","sub":"user-42","aud":"client-1","jti":"id-1","exp":{{exp}},"at_hash":"{{SpecAccessTokenHash}}"}""";
        var compact =
            $"{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header))}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload))}.c2ln";

        var token = TokenJwt.JsonWebToken.Parse(compact);
        Require(token.Subject?.Value == "user-42", "JWT subject parses");
        Require(token.Id == "id-1", "JWT jti projects onto Id");

        var result = token.Validate(new TokenJwt.JsonWebTokenValidationOptions(Now)
        {
            ExpectedIssuer = "https://op.example.com",
            ExpectedAudience = "client-1",
            AccessToken = SpecAccessToken,
        });
        Require(result.Succeeded, "JWT validation (incl. at_hash spec vector) succeeds");
        Console.WriteLine("OK: JWT parse + validate (at_hash spec vector)");
    }

    private static void CheckSamlTokenValidate()
    {
        var descriptor = new TokenSaml.SamlTokenDescriptor
        {
            AssertionId = "_a1",
            Issuer = "https://idp.example.com/saml",
            NameId = new TokenSaml.SamlNameId("user-42", format: SubjectIdentifierFormats.Persistent),
            Conditions = new TokenSaml.SamlConditions(
                notBefore: Now.AddMinutes(-1),
                notOnOrAfter: Now.AddMinutes(5),
                audienceRestrictions: new[] { (IReadOnlyList<string>)new[] { "https://sp.example.com" } }),
        };
        descriptor.SubjectConfirmations.Add(new TokenSaml.SamlSubjectConfirmation(
            TokenSaml.SamlConfirmationMethods.Bearer,
            data: new TokenSaml.SamlSubjectConfirmationData(
                recipient: "https://sp.example.com/acs",
                notOnOrAfter: Now.AddMinutes(5))));

        var token = new TokenSaml.SamlToken(descriptor);
        var result = token.Validate(new TokenSaml.SamlTokenValidationOptions(Now)
        {
            ExpectedIssuer = "https://idp.example.com/saml",
            ExpectedAudience = "https://sp.example.com",
        });
        Require(result.Succeeded, "SAML token validation succeeds");
        Require(token.Subject?.Issuer == "https://idp.example.com/saml", "NameID recipe defaults the qualifier");
        Console.WriteLine("OK: SAML token materialize + validate");
    }

    private static void CheckCrossProtocolCanonicalization()
    {
        // Both protocol contracts asserting the same principal canonicalize to one surface.
        var idTokenDescriptor = new OpenIdConnectIdTokenDescriptor
        {
            Issuer = "https://op.example.com",
            Subject = "user-42",
            ExpiresAt = Now.AddHours(1),
            IssuedAt = Now,
        };
        idTokenDescriptor.Audiences.Add("client-1");
        idTokenDescriptor.AdditionalClaims.Add(new IdentityClaim(
            IdentityClaimTypes.Email,
            "user@example.com",
            "https://op.example.com",
            new IdentityClaimProvenance(AuthenticationProtocol.OpenIdConnect)));
        var oidc = new OpenIdConnectIdToken(idTokenDescriptor).Claims.Canonicalize();

        var assertionDescriptor = new ProtocolSaml.SamlAssertionDescriptor
        {
            Id = "_a1",
            Issuer = new ProtocolSaml.SamlNameId("https://idp.example.com/saml"),
            Subject = new ProtocolSaml.SamlSubject(new ProtocolSaml.SamlNameId("user-42")),
        };
        assertionDescriptor.AttributeStatements.Add(new ProtocolSaml.SamlAttributeStatement(new[]
        {
            new IdentityAttribute(
                "urn:oid:0.9.2342.19200300.100.1.3",
                new[] { IdentityClaimValue.FromString("user@example.com") }),
        }));
        var saml = new ProtocolSaml.SamlAssertion(assertionDescriptor).Claims.Canonicalize();

        Require(
            oidc.GetString(IdentityClaimTypes.Email) == saml.GetString(IdentityClaimTypes.Email),
            "OIDC and SAML canonicalize to one email surface");
        Require(
            saml.TryGet(IdentityClaimTypes.Email, out var email) &&
            email.Provenance?.OriginalType == "urn:oid:0.9.2342.19200300.100.1.3",
            "the original wire name survives canonicalization");
        Console.WriteLine("OK: cross-protocol canonicalization");
    }

    private static void Require(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Check failed: {description}");
        }
    }
}
