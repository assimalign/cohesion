using System;

using Assimalign.Cohesion.IdentityModel;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.IdentityModel.Token.Tests;

/// <summary>
/// Contains unit tests for the shared, root-aligned identity token normalization layer:
/// descriptor snapshotting, canonical-field mapping, claim/provenance handling, audience and
/// temporal semantics, the protocol-neutral validator, and invalid-state guards.
/// </summary>
public sealed class IdentityTokenTests
{
    private static readonly DateTimeOffset now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token] - IdentityToken: The default kind should be Unknown")]
    public void Kind_WhenDefaulted_ShouldBeUnknown()
    {
        default(IdentityTokenKind).ShouldBe(IdentityTokenKind.Unknown);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token] - IdentityToken: Construction should snapshot descriptor values")]
    public void Construct_WhenGivenDescriptor_ShouldSnapshotValues()
    {
        var descriptor = ConformantDescriptor();

        var token = new TestIdentityToken(IdentityTokenKind.JsonWebToken, descriptor);

        // Mutating the descriptor after construction must not change the materialized token.
        descriptor.Audiences.Add("api://billing");
        descriptor.Claims.Add(new IdentityClaim("role", "writer"));
        descriptor.Properties["tenant"] = "mutated";

        token.Kind.ShouldBe(IdentityTokenKind.JsonWebToken);
        token.Protocol.ShouldBe(AuthenticationProtocol.OpenIdConnect);
        token.Id.ShouldBe("token-001");
        token.Issuer.ShouldBe("https://issuer.example.com");
        token.Audiences.Count.ShouldBe(1);
        token.Audiences[0].ShouldBe("api://orders");
        token.Claims.Count.ShouldBe(2);
        token.Properties["tenant"].ShouldBe(IdentityClaimValue.FromString("cohesion"));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token] - IdentityToken: The subject should be the canonical identifier")]
    public void Subject_WhenMapped_ShouldBeCanonicalIdentifier()
    {
        var descriptor = ConformantDescriptor();

        var token = new TestIdentityToken(IdentityTokenKind.JsonWebToken, descriptor);

        token.Subject.ShouldNotBeNull();
        token.Subject.Value.ShouldBe("user-42");
        token.Subject.Issuer.ShouldBe("https://issuer.example.com");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token] - IdentityToken: Claims should use the root lookup vocabulary")]
    public void Claims_WhenQueried_ShouldUseRootVocabulary()
    {
        var descriptor = ConformantDescriptor();
        descriptor.Claims.Add(new IdentityClaim("role", "reader"));
        descriptor.Claims.Add(new IdentityClaim("role", "writer"));
        var token = new TestIdentityToken(IdentityTokenKind.JsonWebToken, descriptor);

        token.Claims.Contains("role").ShouldBeTrue();
        token.Claims.TryGet("role", out var first).ShouldBeTrue();
        first!.Value.ToString().ShouldBe("reader");
        token.Claims.GetAll("role").Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token] - IdentityToken: Claim provenance should survive normalization")]
    public void Claims_WhenBuilt_ShouldCarryProvenance()
    {
        var descriptor = ConformantDescriptor();
        descriptor.Claims.Add(new IdentityClaim(
            "email",
            "user@example.com",
            issuer: "https://issuer.example.com",
            provenance: new IdentityClaimProvenance(AuthenticationProtocol.OpenIdConnect, originalType: "email")));
        var token = new TestIdentityToken(IdentityTokenKind.JsonWebToken, descriptor);

        token.Claims.TryGet("email", out var email).ShouldBeTrue();
        email!.Provenance!.Protocol.ShouldBe(AuthenticationProtocol.OpenIdConnect);
        email.Provenance.OriginalType.ShouldBe("email");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token] - IdentityToken: An undefined property value should be rejected")]
    public void Construct_WhenPropertyValueUndefined_ShouldThrow()
    {
        var descriptor = ConformantDescriptor();
        descriptor.Properties["bad"] = default; // Undefined — the fail-closed rule rejects it.

        Should.Throw<ArgumentException>(() => new TestIdentityToken(IdentityTokenKind.JsonWebToken, descriptor));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token] - IdentityToken: Audiences should not expose a mutable backing array")]
    public void Audiences_WhenExposed_ShouldNotBeMutable()
    {
        var token = new TestIdentityToken(IdentityTokenKind.JsonWebToken, ConformantDescriptor());

        // The exposed list must not be the interior array (which a consumer could cast back
        // and mutate, corrupting the audience check).
        (token.Audiences as string[]).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token] - IdentityToken: The authentication context should project onto the token")]
    public void AuthenticationContext_WhenSet_ShouldProjectOntoToken()
    {
        var contextDescriptor = new AuthenticationContextDescriptor
        {
            AuthenticatedAt = now.AddMinutes(-1),
            ContextClass = "urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport",
        };
        contextDescriptor.Methods.Add("pwd");
        contextDescriptor.ProviderSessionIds.Add("sess-1");

        var descriptor = ConformantDescriptor();
        descriptor.AuthenticationContext = new AuthenticationContext(contextDescriptor);
        var token = new TestIdentityToken(IdentityTokenKind.JsonWebToken, descriptor);

        token.AuthenticationContext.ShouldNotBeNull();
        token.AuthenticationContext.ContextClass.ShouldBe("urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport");
        token.AuthenticationContext.Methods.ShouldContain("pwd");
        token.AuthenticationContext.ProviderSessionIds.ShouldContain("sess-1");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token] - IdentityToken: The error severity should be the fail-closed default")]
    public void TokenValidationSeverity_Error_ShouldBeFailClosedDefault()
    {
        ((int)TokenValidationSeverity.Error).ShouldBe(0);
        default(TokenValidationSeverity).ShouldBe(TokenValidationSeverity.Error);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token] - IdentityToken: HasAudience should match ordinally")]
    public void HasAudience_WhenPresent_ShouldMatch()
    {
        var token = new TestIdentityToken(IdentityTokenKind.JsonWebToken, ConformantDescriptor());

        token.HasAudience("api://orders").ShouldBeTrue();
        token.HasAudience("api://other").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token] - IdentityToken: Temporal helpers should honor the window and skew")]
    public void TemporalHelpers_WhenEvaluated_ShouldHonorWindowAndSkew()
    {
        var token = new TestIdentityToken(IdentityTokenKind.JsonWebToken, ConformantDescriptor());

        token.IsActive(now).ShouldBeTrue();
        token.IsExpired(now).ShouldBeFalse();

        // One minute past expiry, but within a five-minute skew, is still active.
        var justAfter = now.AddMinutes(56);
        token.IsExpired(justAfter).ShouldBeTrue();
        token.IsActive(justAfter, TimeSpan.FromMinutes(5)).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token] - IdentityToken: A conformant token should validate")]
    public void Validate_WhenConformant_ShouldSucceed()
    {
        var token = new TestIdentityToken(IdentityTokenKind.JsonWebToken, ConformantDescriptor());

        var result = token.Validate(new IdentityTokenValidationOptions(now)
        {
            ExpectedIssuer = "https://issuer.example.com",
            ExpectedAudience = "api://orders",
        });

        result.Succeeded.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token] - IdentityToken: A wrong issuer should fail")]
    public void Validate_WhenIssuerMismatched_ShouldReportIssuerMismatch()
    {
        var token = new TestIdentityToken(IdentityTokenKind.JsonWebToken, ConformantDescriptor());

        var result = token.Validate(new IdentityTokenValidationOptions(now)
        {
            ExpectedIssuer = "https://attacker.example.com",
        });

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == TokenValidationCodes.IssuerMismatch);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token] - IdentityToken: A wrong audience should fail")]
    public void Validate_WhenAudienceMismatched_ShouldReportAudienceMismatch()
    {
        var token = new TestIdentityToken(IdentityTokenKind.JsonWebToken, ConformantDescriptor());

        var result = token.Validate(new IdentityTokenValidationOptions(now)
        {
            ExpectedAudience = "api://not-this-one",
        });

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == TokenValidationCodes.AudienceMismatch);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token] - IdentityToken: An expired token should fail")]
    public void Validate_WhenExpired_ShouldReportExpired()
    {
        var token = new TestIdentityToken(IdentityTokenKind.JsonWebToken, ConformantDescriptor());

        var result = token.Validate(new IdentityTokenValidationOptions(now.AddHours(2)));

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == TokenValidationCodes.Expired);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token] - IdentityToken: A not-yet-valid token should fail")]
    public void Validate_WhenNotYetValid_ShouldReportNotYetValid()
    {
        var token = new TestIdentityToken(IdentityTokenKind.JsonWebToken, ConformantDescriptor());

        var result = token.Validate(new IdentityTokenValidationOptions(now.AddHours(-2)));

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == TokenValidationCodes.NotYetValid);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token] - IdentityToken: Clock skew should forgive a just-expired token")]
    public void Validate_WhenExpiredWithinSkew_ShouldSucceed()
    {
        var token = new TestIdentityToken(IdentityTokenKind.JsonWebToken, ConformantDescriptor());

        // Expiry is now+55m; validating at now+57m is past expiry but within a five-minute skew.
        var result = token.Validate(new IdentityTokenValidationOptions(now.AddMinutes(57))
        {
            ClockSkew = TimeSpan.FromMinutes(5),
        });

        result.Succeeded.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token] - IdentityToken: A JWT-derived and a SAML-derived token should resolve to the same canonical surface")]
    public void Compatibility_WhenJwtAndSamlCarrySameSubject_ShouldResolveEqually()
    {
        // A JWT ID token and a SAML assertion asserting the same principal and the same email
        // must normalize to equal canonical surfaces — the shared-normalization contract.
        // The subjects are built as distinct instances so equality proves value-based
        // normalization, not reference pass-through.
        var jwt = new TestIdentityToken(IdentityTokenKind.JsonWebToken, new IdentityTokenDescriptor
        {
            Protocol = AuthenticationProtocol.OpenIdConnect,
            Issuer = "https://issuer.example.com",
            Subject = new SubjectIdentifier("user-42", issuer: "https://issuer.example.com"),
        }.WithClaim("email", "user@example.com"));

        var saml = new TestIdentityToken(IdentityTokenKind.Saml, new IdentityTokenDescriptor
        {
            Protocol = AuthenticationProtocol.Saml2,
            Issuer = "https://issuer.example.com",
            Subject = new SubjectIdentifier("user-42", issuer: "https://issuer.example.com"),
        }.WithClaim("email", "user@example.com"));

        jwt.Subject.ShouldBe(saml.Subject);
        ReferenceEquals(jwt.Subject, saml.Subject).ShouldBeFalse();
        jwt.Claims.GetValues("email").ShouldBe(saml.Claims.GetValues("email"));
        jwt.Protocol.ShouldNotBe(saml.Protocol); // provenance still distinguishes them.
    }

    private static IdentityTokenDescriptor ConformantDescriptor()
    {
        var descriptor = new IdentityTokenDescriptor
        {
            Protocol = AuthenticationProtocol.OpenIdConnect,
            Id = "token-001",
            Subject = new SubjectIdentifier("user-42", issuer: "https://issuer.example.com"),
            Issuer = "https://issuer.example.com",
            TokenType = "Bearer",
            RawData = "raw-token-value",
            IssuedAt = now,
            NotBefore = now.AddMinutes(-5),
            ExpiresAt = now.AddMinutes(55),
        };

        descriptor.Audiences.Add("api://orders");
        descriptor.Claims.Add(new IdentityClaim("sub", "user-42"));
        descriptor.Claims.Add(new IdentityClaim("scope", "read"));
        descriptor.Properties["tenant"] = IdentityClaimValue.FromString("cohesion");
        return descriptor;
    }

    private sealed class TestIdentityToken : IdentityToken
    {
        public TestIdentityToken(IdentityTokenKind kind, IdentityTokenDescriptor descriptor)
            : base(kind, descriptor)
        {
        }
    }
}

/// <summary>
/// A small fluent helper so the compatibility fixture reads cleanly.
/// </summary>
internal static class DescriptorTestExtensions
{
    extension(IdentityTokenDescriptor descriptor)
    {
        public IdentityTokenDescriptor WithClaim(string type, IdentityClaimValue value)
        {
            descriptor.Claims.Add(new IdentityClaim(type, value));
            return descriptor;
        }
    }
}
