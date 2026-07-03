using System;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Proves the feature-level acceptance criterion for the canonical domain model: the same
/// logical principal can be represented from OpenID Connect data and from SAML 2.0 data with
/// equal fidelity, without losing protocol provenance, and without either wire format being
/// privileged.
/// </summary>
public sealed class CanonicalModelFidelityTests
{
    private static readonly DateTimeOffset now = new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Fidelity: An OIDC-authenticated principal should map without loss")]
    public void CanonicalModel_WhenSourcedFromOidc_ShouldPreserveProvenance()
    {
        // Arrange — normalize an ID-token-shaped principal (iss/sub + profile claims).
        var descriptor = new IdentitySubjectDescriptor
        {
            Kind = IdentityKind.User,
            Identifier = new SubjectIdentifier(
                value: "248289761001",
                format: SubjectIdentifierFormats.Pairwise,
                issuer: "https://op.example",
                relyingPartyQualifier: "https://rp.example/sector"),
            DisplayName = "Jane Doe",
        };
        descriptor.Claims.Add(new IdentityClaim(
            IdentityClaimTypes.Email,
            "jane@example.com",
            issuer: "https://op.example",
            provenance: new IdentityClaimProvenance(AuthenticationProtocol.OpenIdConnect, originalType: "email")));

        var contextDescriptor = new AuthenticationContextDescriptor
        {
            AuthenticatedAt = now.AddSeconds(-30),
            ContextClass = "urn:mace:incommon:iap:silver",
        };
        contextDescriptor.Methods.Add("pwd");
        contextDescriptor.Methods.Add("otp");
        contextDescriptor.ProviderSessionIds.Add("op-sid-77");

        // Act
        var result = new AuthenticationResult(new AuthenticationResultDescriptor
        {
            Subject = new IdentitySubject(descriptor),
            Protocol = AuthenticationProtocol.OpenIdConnect,
            CompletedAt = now,
            Issuer = "https://op.example",
            Audience = "s6BhdRkqt3",
            EvidenceId = "jti-a41c",
            Context = new AuthenticationContext(contextDescriptor),
        });

        // Assert — everything an RP must persist survives normalization.
        result.Subject!.Identifier.RelyingPartyQualifier.ShouldBe("https://rp.example/sector");
        result.Subject.Claims.TryGet(IdentityClaimTypes.Email, out var email).ShouldBeTrue();
        email!.Provenance!.Protocol.ShouldBe(AuthenticationProtocol.OpenIdConnect);
        result.Context!.Methods.ShouldBe(["pwd", "otp"]);
        result.Context.ProviderSessionIds.ShouldBe(["op-sid-77"]);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Fidelity: A SAML-asserted principal should map without loss")]
    public void CanonicalModel_WhenSourcedFromSaml_ShouldPreserveProvenance()
    {
        // Arrange — normalize an assertion-shaped principal (persistent NameID + attribute).
        var descriptor = new IdentitySubjectDescriptor
        {
            Kind = IdentityKind.User,
            Identifier = new SubjectIdentifier(
                value: "x9f2-persistent",
                format: SubjectIdentifierFormats.Persistent,
                issuer: "https://idp.example",
                relyingPartyQualifier: "https://sp.example"),
        };
        descriptor.Claims.Add(new IdentityClaim(
            IdentityClaimTypes.Email,
            "jane@example.com",
            issuer: "https://idp.example",
            provenance: new IdentityClaimProvenance(
                AuthenticationProtocol.Saml2,
                originalType: "urn:oid:0.9.2342.19200300.100.1.3",
                originalNameFormat: "urn:oasis:names:tc:SAML:2.0:attrname-format:uri",
                originalValueType: "xs:string",
                originalFriendlyName: "mail")));

        var contextDescriptor = new AuthenticationContextDescriptor
        {
            AuthenticatedAt = now.AddSeconds(-30),
            ContextClass = "urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport",
            SessionExpiresAt = now.AddHours(8),
        };
        contextDescriptor.ProviderSessionIds.Add("session-index-3");
        contextDescriptor.AuthenticatingAuthorities.Add("https://upstream-idp.example");

        // Act
        var result = new AuthenticationResult(new AuthenticationResultDescriptor
        {
            Subject = new IdentitySubject(descriptor),
            Protocol = AuthenticationProtocol.Saml2,
            CompletedAt = now,
            Issuer = "https://idp.example",
            Audience = "https://sp.example",
            EvidenceId = "_assertion-8fe1",
            Context = new AuthenticationContext(contextDescriptor),
        });

        // Assert — the session built from this result can honor single logout, and the
        // original SAML attribute identity is fully recoverable from provenance.
        var email = result.Subject!.Claims.GetAll(IdentityClaimTypes.Email)[0];
        email.Provenance!.OriginalType.ShouldBe("urn:oid:0.9.2342.19200300.100.1.3");
        email.Provenance.OriginalFriendlyName.ShouldBe("mail");
        email.Provenance.OriginalNameFormat.ShouldBe("urn:oasis:names:tc:SAML:2.0:attrname-format:uri");
        result.Context!.SessionExpiresAt.ShouldBe(now.AddHours(8));
        result.Context.AuthenticatingAuthorities.ShouldBe(["https://upstream-idp.example"]);

        var session = new AuthenticationSession(new AuthenticationSessionDescriptor
        {
            SessionId = "sp-session-1",
            Subject = result.Subject.Identifier,
            SubjectKind = result.Subject.Kind,
            Protocol = result.Protocol,
            Issuer = result.Issuer,
            CreatedAt = result.CompletedAt,
            ExpiresAt = result.Context.SessionExpiresAt,
            State = AuthenticationSessionState.Active,
        });
        session.Issuer.ShouldBe("https://idp.example");
        session.IsActive(now).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Fidelity: The two protocols should produce the same consumer surface")]
    public void CanonicalModel_AcrossProtocols_ShouldExposeOneConsumerSurface()
    {
        // Arrange — the same logical role data arriving via each protocol's natural shape.
        var oidcSubject = new IdentitySubjectDescriptor
        {
            Kind = IdentityKind.User,
            Identifier = new SubjectIdentifier("sub-1", issuer: "https://op.example"),
        };
        oidcSubject.Claims.Add(new IdentityClaim(
            IdentityClaimTypes.Roles,
            IdentityClaimValue.FromArray([IdentityClaimValue.FromString("reader"), IdentityClaimValue.FromString("admin")]),
            provenance: new IdentityClaimProvenance(AuthenticationProtocol.OpenIdConnect)));

        var samlSubject = new IdentitySubjectDescriptor
        {
            Kind = IdentityKind.User,
            Identifier = new SubjectIdentifier("nameid-1", SubjectIdentifierFormats.Persistent, "https://idp.example"),
        };
        samlSubject.Claims.Add(new IdentityClaim(IdentityClaimTypes.Roles, "reader",
            provenance: new IdentityClaimProvenance(AuthenticationProtocol.Saml2)));
        samlSubject.Claims.Add(new IdentityClaim(IdentityClaimTypes.Roles, "admin",
            provenance: new IdentityClaimProvenance(AuthenticationProtocol.Saml2)));

        // Act
        var fromOidc = new IdentitySubject(oidcSubject);
        var fromSaml = new IdentitySubject(samlSubject);

        // Assert — an authorization check written once works for both, while provenance
        // still tells the protocols apart.
        fromOidc.Claims.HasClaim(IdentityClaimTypes.Roles, "admin").ShouldBeTrue();
        fromSaml.Claims.HasClaim(IdentityClaimTypes.Roles, "admin").ShouldBeTrue();
        fromOidc.Claims.GetValues(IdentityClaimTypes.Roles).Count.ShouldBe(2);
        fromSaml.Claims.GetValues(IdentityClaimTypes.Roles).Count.ShouldBe(2);
        fromOidc.Claims.GetAll(IdentityClaimTypes.Roles)[0].Provenance!.Protocol.ShouldBe(AuthenticationProtocol.OpenIdConnect);
        fromSaml.Claims.GetAll(IdentityClaimTypes.Roles)[0].Provenance!.Protocol.ShouldBe(AuthenticationProtocol.Saml2);
    }
}
