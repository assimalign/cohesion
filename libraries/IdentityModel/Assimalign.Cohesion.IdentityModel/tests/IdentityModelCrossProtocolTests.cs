using System;
using System.Linq;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

using ProtocolSaml = Assimalign.Cohesion.IdentityModel.Protocols.Saml;
using TokenJwt = Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;
using TokenSaml = Assimalign.Cohesion.IdentityModel.Token.Saml;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Cross-protocol compatibility and migration fixtures: paired OpenID Connect and SAML inputs
/// asserting the same principal must canonicalize to one normalized identity surface — while
/// non-equivalent concepts stay explicitly unmapped rather than being forced into false
/// equivalence. This suite is the long-lived regression corpus for future protocol additions.
/// </summary>
public sealed class IdentityModelCrossProtocolTests
{
    private static readonly DateTimeOffset now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    private const string OidcIssuer = "https://op.example.com";
    private const string SamlIssuer = "https://idp.example.com/saml";
    private const string MailOid = "urn:oid:0.9.2342.19200300.100.1.3";

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - CrossProtocol: OIDC and SAML contracts canonicalize to one surface")]
    public void ProtocolContracts_WhenCanonicalized_ShouldAgreeOnTheCanonicalSurface()
    {
        var oidc = ConformantIdToken().Claims.Canonicalize();
        var saml = ConformantAssertion().Claims.Canonicalize();

        // The same principal data reads identically through the canonical vocabulary.
        foreach (var type in new[]
        {
            IdentityClaimTypes.Email,
            IdentityClaimTypes.GivenName,
            IdentityClaimTypes.FamilyName,
            IdentityClaimTypes.Name,
        })
        {
            saml.GetString(type).ShouldBe(oidc.GetString(type), $"canonical '{type}' must agree");
        }

        oidc.GetString(IdentityClaimTypes.Subject).ShouldBe("user-42");
        saml.GetString(IdentityClaimTypes.Subject).ShouldBe("user-42");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - CrossProtocol: Provenance stays distinct and lossless across the shared surface")]
    public void ProtocolContracts_WhenCanonicalized_ShouldPreserveDistinctProvenance()
    {
        var oidc = ConformantIdToken().Claims.Canonicalize();
        var saml = ConformantAssertion().Claims.Canonicalize();

        oidc.TryGet(IdentityClaimTypes.Email, out var oidcEmail).ShouldBeTrue();
        oidcEmail!.Provenance!.Protocol.ShouldBe(AuthenticationProtocol.OpenIdConnect);

        saml.TryGet(IdentityClaimTypes.Email, out var samlEmail).ShouldBeTrue();
        samlEmail!.Provenance!.Protocol.ShouldBe(AuthenticationProtocol.Saml2);
        // The original wire name survives the remap — normalization never erases the source.
        samlEmail.Provenance.OriginalType.ShouldBe(MailOid);
        samlEmail.Provenance.OriginalFriendlyName.ShouldBe("mail");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - CrossProtocol: Token documents canonicalize to one surface")]
    public void TokenDocuments_WhenCanonicalized_ShouldAgreeOnTheCanonicalSurface()
    {
        // The token branch pairing: a JWT document and a SAML assertion token.
        var jwtDescriptor = new TokenJwt.JsonWebTokenDescriptor
        {
            Protocol = AuthenticationProtocol.OpenIdConnect,
            Issuer = OidcIssuer,
            Subject = new SubjectIdentifier("user-42", issuer: OidcIssuer),
        };
        jwtDescriptor.Claims.Add(new IdentityClaim(IdentityClaimTypes.Subject, "user-42"));
        jwtDescriptor.Claims.Add(new IdentityClaim(IdentityClaimTypes.Email, "user@example.com"));
        var jwt = new TokenJwt.JsonWebToken(jwtDescriptor);

        var samlDescriptor = new TokenSaml.SamlTokenDescriptor
        {
            AssertionId = "_a1",
            Issuer = SamlIssuer,
            NameId = new TokenSaml.SamlNameId("user-42"),
        };
        samlDescriptor.Attributes.Add(new IdentityAttribute(
            MailOid,
            new[] { IdentityClaimValue.FromString("user@example.com") }));
        var saml = new TokenSaml.SamlToken(samlDescriptor);

        var jwtCanonical = jwt.Claims.Canonicalize();
        var samlCanonical = saml.Claims.Canonicalize();

        samlCanonical.GetString(IdentityClaimTypes.Email).ShouldBe(jwtCanonical.GetString(IdentityClaimTypes.Email));
        samlCanonical.GetString(IdentityClaimTypes.Subject).ShouldBe(jwtCanonical.GetString(IdentityClaimTypes.Subject));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - CrossProtocol: Identifier-shaped attributes never mint a second subject")]
    public void Canonicalize_WhenIdentifierShapedAttributesPresent_ShouldNotTouchTheSubject()
    {
        // ADFS-style assertions commonly carry nameidentifier/UPN/ePPN as ATTRIBUTES while the
        // subject already flows from the NameID recipe. None may remap; sub stays single.
        var descriptor = ConformantAssertionDescriptor();
        descriptor.AttributeStatements.Add(new ProtocolSaml.SamlAttributeStatement(new[]
        {
            Attribute("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier", "user-42"),
            Attribute("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn", "user-42@corp.example.com"),
            Attribute("urn:oid:1.3.6.1.4.1.5923.1.1.1.6", "user-42@example.edu"), // eduPersonPrincipalName
        }));

        var canonical = new ProtocolSaml.SamlAssertion(descriptor).Claims.Canonicalize();

        canonical.GetAll(IdentityClaimTypes.Subject).Count.ShouldBe(1);
        canonical.Contains("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier").ShouldBeTrue();
        canonical.Contains("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn").ShouldBeTrue();
        canonical.Contains("urn:oid:1.3.6.1.4.1.5923.1.1.1.6").ShouldBeTrue();
        canonical.Contains(IdentityClaimTypes.PreferredUsername).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - CrossProtocol: Ambiguous and vendor-shaped names stay raw")]
    public void Canonicalize_WhenAmbiguousNamesPresent_ShouldPassThemThroughRaw()
    {
        var descriptor = ConformantAssertionDescriptor();
        descriptor.AttributeStatements.Add(new ProtocolSaml.SamlAttributeStatement(new[]
        {
            // WS-Fed …/claims/name is a UPN/account name in practice — not the OIDC 'name'.
            Attribute("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name", "CORP\\user42"),
            // eduPersonAffiliation is an affiliation vocabulary, not group membership.
            Attribute("urn:oid:1.3.6.1.4.1.5923.1.1.1.1", "staff"),
            // LDAP cn is ambiguous (often not a display name).
            Attribute("urn:oid:2.5.4.3", "user42"),
            // eduPersonTargetedID is NameID-valued and deprecated.
            Attribute("urn:oid:1.3.6.1.4.1.5923.1.1.1.10", "opaque!pair!wise"),
            // Vendor role URIs are vendor-shaped (GUIDs vs names), not RFC 9068 roles.
            Attribute("http://schemas.microsoft.com/ws/2008/06/identity/claims/role", "Admin"),
            // Bare basic-format short names collide with the open extension-claim space.
            Attribute("mail", "short@example.com"),
        }));

        var canonical = new ProtocolSaml.SamlAssertion(descriptor).Claims.Canonicalize();

        canonical.Contains("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name").ShouldBeTrue();
        canonical.Contains("urn:oid:1.3.6.1.4.1.5923.1.1.1.1").ShouldBeTrue();
        canonical.Contains("urn:oid:2.5.4.3").ShouldBeTrue();
        canonical.Contains("urn:oid:1.3.6.1.4.1.5923.1.1.1.10").ShouldBeTrue();
        canonical.Contains("http://schemas.microsoft.com/ws/2008/06/identity/claims/role").ShouldBeTrue();
        canonical.Contains("mail").ShouldBeTrue();
        canonical.Contains(IdentityClaimTypes.Groups).ShouldBeFalse();
        canonical.Contains(IdentityClaimTypes.Roles).ShouldBeFalse();
        // The strict mail OID mapping still applied; the bare short name did not double-map.
        canonical.GetAll(IdentityClaimTypes.Email).Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - CrossProtocol: Non-equivalent concepts are never fabricated")]
    public void Canonicalize_WhenConceptHasNoEquivalent_ShouldNotFabricateIt()
    {
        var saml = new ProtocolSaml.SamlAssertion(ConformantAssertionDescriptor()).Claims.Canonicalize();

        // SAML asserts no verification or structured-address concepts, and
        // authentication-statement data flows through AuthenticationContext — never
        // fabricated as claims.
        saml.Contains(IdentityClaimTypes.EmailVerified).ShouldBeFalse();
        saml.Contains(IdentityClaimTypes.PhoneNumberVerified).ShouldBeFalse();
        saml.Contains("address").ShouldBeFalse();
        saml.Contains("acr").ShouldBeFalse();
        saml.Contains("auth_time").ShouldBeFalse();
    }

    private static OpenIdConnectIdToken ConformantIdToken()
    {
        var descriptor = new OpenIdConnectIdTokenDescriptor
        {
            Issuer = OidcIssuer,
            Subject = "user-42",
            ExpiresAt = now.AddHours(1),
            IssuedAt = now,
        };
        descriptor.Audiences.Add("client-1");

        // Extension claims are fully-formed root claims — a real materializer stamps them
        // with OIDC provenance, exactly as the typed members are.
        var provenance = new IdentityClaimProvenance(AuthenticationProtocol.OpenIdConnect, originalIssuer: OidcIssuer);
        descriptor.AdditionalClaims.Add(new IdentityClaim(IdentityClaimTypes.Email, "user@example.com", OidcIssuer, provenance));
        descriptor.AdditionalClaims.Add(new IdentityClaim(IdentityClaimTypes.GivenName, "Ada", OidcIssuer, provenance));
        descriptor.AdditionalClaims.Add(new IdentityClaim(IdentityClaimTypes.FamilyName, "Lovelace", OidcIssuer, provenance));
        descriptor.AdditionalClaims.Add(new IdentityClaim(IdentityClaimTypes.Name, "Ada Lovelace", OidcIssuer, provenance));
        return new OpenIdConnectIdToken(descriptor);
    }

    private static ProtocolSaml.SamlAssertion ConformantAssertion()
        => new(ConformantAssertionDescriptor());

    private static ProtocolSaml.SamlAssertionDescriptor ConformantAssertionDescriptor()
    {
        var descriptor = new ProtocolSaml.SamlAssertionDescriptor
        {
            Id = "_a1",
            Version = ProtocolSaml.SamlConstants.Version,
            IssueInstant = now,
            Issuer = new ProtocolSaml.SamlNameId(SamlIssuer, ProtocolSaml.SamlNameIdFormats.Entity),
            Subject = new ProtocolSaml.SamlSubject(new ProtocolSaml.SamlNameId("user-42")),
        };

        descriptor.AuthnStatements.Add(new ProtocolSaml.SamlAuthnStatement(
            new ProtocolSaml.SamlAuthnContext(ProtocolSaml.SamlAuthnContextClasses.PasswordProtectedTransport),
            authnInstant: now.AddMinutes(-1)));

        descriptor.AttributeStatements.Add(new ProtocolSaml.SamlAttributeStatement(new[]
        {
            new IdentityAttribute(
                MailOid,
                new[] { IdentityClaimValue.FromString("user@example.com") },
                nameFormat: ProtocolSaml.SamlAttributeNameFormats.Uri,
                friendlyName: "mail"),
            Attribute("urn:oid:2.5.4.42", "Ada"),
            Attribute("urn:oid:2.5.4.4", "Lovelace"),
            Attribute("urn:oid:2.16.840.1.113730.3.1.241", "Ada Lovelace"),
        }));

        return descriptor;
    }

    private static IdentityAttribute Attribute(string name, string value)
        => new(name, new[] { IdentityClaimValue.FromString(value) });
}
