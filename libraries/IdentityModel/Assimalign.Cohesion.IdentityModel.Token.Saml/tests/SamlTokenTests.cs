using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.IdentityModel.Token.Saml.Tests;

/// <summary>
/// Contains unit tests and descriptor-built fixtures for the SAML assertion token: normalization
/// onto the canonical model, the typed SAML structure, and the token-substrate validation
/// (composed neutral rules + the bearer confirmation-data window). "Malformed" fixtures are
/// malformed-descriptor (semantic) cases, since XML-level malformation is a deferred parser seam.
/// </summary>
public sealed class SamlTokenTests
{
    private static readonly DateTimeOffset now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    private const string Issuer = "https://idp.example.com/saml";
    private const string RelyingParty = "https://sp.example.com";
    private const string AcsUrl = "https://sp.example.com/acs";
    private const string RequestId = "_req-1";

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.Saml] - SamlToken: Materializes the normalized surface from the typed structure")]
    public void Construct_WhenGivenAssertion_ShouldNormalizeOntoRoot()
    {
        var token = new SamlToken(ConformantDescriptor());

        token.Kind.ShouldBe(IdentityTokenKind.Saml);
        token.Protocol.ShouldBe(AuthenticationProtocol.Saml2);
        token.Id.ShouldBe("_a1");
        token.Issuer.ShouldBe(Issuer);
        // Subject lifted through the recipe (issuer defaulted onto the qualifier).
        token.Subject.ShouldNotBeNull();
        token.Subject.Value.ShouldBe("user@example.com");
        token.Subject.Issuer.ShouldBe(Issuer);
        token.Subject.RelyingPartyQualifier.ShouldBe(RelyingParty);
        // Temporal comes from Conditions, not the bearer window.
        token.NotBefore.ShouldBe(now.AddMinutes(-1));
        token.ExpiresAt.ShouldBe(now.AddMinutes(5));
        token.AuthenticationContext.ShouldNotBeNull();
        token.AuthenticationContext.ProviderSessionIds.ShouldContain("sess-1");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.Saml] - SamlToken: Preserves the typed SAML structure")]
    public void Construct_WhenGivenAssertion_ShouldPreserveTypedStructure()
    {
        var descriptor = ConformantDescriptor();
        descriptor.EncryptedId = new SamlEncryptedElement(rawXml: "<EncryptedID/>");
        var token = new SamlToken(descriptor);

        token.AssertionId.ShouldBe("_a1");
        token.Version.ShouldBe("2.0");
        token.NameId.ShouldNotBeNull();
        token.NameId.Format.ShouldBe(SubjectIdentifierFormats.EmailAddress);
        token.Conditions.ShouldNotBeNull();
        token.SubjectConfirmations.Count.ShouldBe(1);
        token.SubjectConfirmations[0].IsBearer.ShouldBeTrue();
        token.EncryptedId.ShouldNotBeNull();
        token.AssertionXml.ShouldBe("<Assertion ID=\"_a1\" />");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.Saml] - SamlToken: Claims carry sub and one claim per attribute value with provenance")]
    public void Claims_WhenBuilt_ShouldCarrySubjectAndAttributeValues()
    {
        var token = new SamlToken(ConformantDescriptor());

        token.Claims.TryGet(IdentityClaimTypes.Subject, out var sub).ShouldBeTrue();
        sub!.Value.ToString().ShouldBe("user@example.com");
        sub.Provenance!.Protocol.ShouldBe(AuthenticationProtocol.Saml2);

        token.Claims.TryGet("urn:oid:email", out var email).ShouldBeTrue();
        email!.Provenance!.OriginalNameFormat.ShouldBe("uri");
        email.Provenance.OriginalFriendlyName.ShouldBe("email");

        // Multi-value attribute becomes duplicate claims.
        token.Claims.GetAll("urn:oid:groups").Select(c => c.Value.ToString()).ShouldBe(new[] { "staff", "admin" });
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.Saml] - SamlToken: A conformant token validates")]
    public void Validate_WhenConformant_ShouldSucceed()
    {
        var token = new SamlToken(ConformantDescriptor());

        token.Validate(ConformantOptions()).Succeeded.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.Saml] - SamlToken: An expired conditions window fails via the base")]
    public void Validate_WhenConditionsExpired_ShouldReportExpired()
    {
        var descriptor = ConformantDescriptor();
        descriptor.Conditions = new SamlConditions(
            notBefore: now.AddMinutes(-10),
            notOnOrAfter: now.AddMinutes(-5),
            audienceRestrictions: Audience(RelyingParty));
        var token = new SamlToken(descriptor);

        var result = token.Validate(ConformantOptions());

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == TokenValidationCodes.Expired);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.Saml] - SamlToken: The AND-across audience rule is enforced")]
    public void Validate_WhenAudienceRestrictionsAnded_ShouldRequireEvery()
    {
        // Two restrictions: (sp) AND (other). A relying party only in the first must fail.
        var descriptor = ConformantDescriptor();
        descriptor.Conditions = new SamlConditions(
            notBefore: now.AddMinutes(-1),
            notOnOrAfter: now.AddMinutes(5),
            audienceRestrictions: new[]
            {
                (IReadOnlyList<string>)new[] { RelyingParty },
                (IReadOnlyList<string>)new[] { "https://other.example.com" },
            });
        var token = new SamlToken(descriptor);

        var result = token.Validate(ConformantOptions());

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == TokenValidationCodes.AudienceMismatch);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.Saml] - SamlToken: A stale bearer confirmation fails")]
    public void Validate_WhenBearerWindowExpired_ShouldReportSubjectConfirmationInvalid()
    {
        var descriptor = ConformantDescriptor();
        descriptor.SubjectConfirmations.Clear();
        descriptor.SubjectConfirmations.Add(new SamlSubjectConfirmation(
            SamlConfirmationMethods.Bearer,
            data: new SamlSubjectConfirmationData(recipient: AcsUrl, notOnOrAfter: now.AddMinutes(-10), inResponseTo: RequestId)));
        var token = new SamlToken(descriptor);

        var result = token.Validate(ConformantOptions());

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == SamlTokenValidationCodes.SubjectConfirmationInvalid);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.Saml] - SamlToken: A bearer confirmation with NotBefore is invalid")]
    public void Validate_WhenBearerHasNotBefore_ShouldReportSubjectConfirmationInvalid()
    {
        var descriptor = ConformantDescriptor();
        descriptor.SubjectConfirmations.Clear();
        descriptor.SubjectConfirmations.Add(new SamlSubjectConfirmation(
            SamlConfirmationMethods.Bearer,
            data: new SamlSubjectConfirmationData(
                recipient: AcsUrl,
                notBefore: now.AddMinutes(-1), // the bearer profile forbids NotBefore
                notOnOrAfter: now.AddMinutes(5),
                inResponseTo: RequestId)));
        var token = new SamlToken(descriptor);

        token.Validate(ConformantOptions()).Errors
            .ShouldContain(e => e.Code == SamlTokenValidationCodes.SubjectConfirmationInvalid);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.Saml] - SamlToken: A recipient mismatch fails the bearer window")]
    public void Validate_WhenRecipientMismatched_ShouldReportSubjectConfirmationInvalid()
    {
        var token = new SamlToken(ConformantDescriptor());

        var options = ConformantOptions();
        options.ExpectedRecipient = "https://sp.example.com/other";

        token.Validate(options).Errors.ShouldContain(e => e.Code == SamlTokenValidationCodes.SubjectConfirmationInvalid);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.Saml] - SamlToken: An absent bearer confirmation only fails when required")]
    public void Validate_WhenNoBearerConfirmation_ShouldHonorRequireFlag()
    {
        var descriptor = ConformantDescriptor();
        descriptor.SubjectConfirmations.Clear();
        var token = new SamlToken(descriptor);

        // Default: the token layer does not impose the require-a-bearer posture.
        token.Validate(ConformantOptions()).Succeeded.ShouldBeTrue();

        var options = ConformantOptions();
        options.RequireBearerConfirmation = true;
        token.Validate(options).Errors.ShouldContain(e => e.Code == SamlTokenValidationCodes.SubjectConfirmationInvalid);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.Saml] - SamlToken: The NameID recipe defaults the qualifier to the issuer")]
    public void Subject_WhenNameQualifierAbsent_ShouldDefaultToIssuer()
    {
        var descriptor = ConformantDescriptor();
        descriptor.NameId = new SamlNameId("user@example.com", format: SubjectIdentifierFormats.EmailAddress);
        var token = new SamlToken(descriptor);

        token.Subject!.Issuer.ShouldBe(Issuer);
    }

    private static SamlTokenDescriptor ConformantDescriptor()
    {
        var contextDescriptor = new AuthenticationContextDescriptor
        {
            AuthenticatedAt = now.AddMinutes(-5),
            ContextClass = "urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport",
        };
        contextDescriptor.ProviderSessionIds.Add("sess-1");

        var descriptor = new SamlTokenDescriptor
        {
            AssertionId = "_a1",
            Version = "2.0",
            Issuer = Issuer,
            IssuedAt = now,
            AssertionXml = "<Assertion ID=\"_a1\" />",
            NameId = new SamlNameId(
                "user@example.com",
                format: SubjectIdentifierFormats.EmailAddress,
                nameQualifier: Issuer,
                spNameQualifier: RelyingParty),
            Conditions = new SamlConditions(
                notBefore: now.AddMinutes(-1),
                notOnOrAfter: now.AddMinutes(5),
                audienceRestrictions: Audience(RelyingParty)),
            AuthenticationContext = new AuthenticationContext(contextDescriptor),
        };

        descriptor.SubjectConfirmations.Add(new SamlSubjectConfirmation(
            SamlConfirmationMethods.Bearer,
            data: new SamlSubjectConfirmationData(recipient: AcsUrl, notOnOrAfter: now.AddMinutes(5), inResponseTo: RequestId)));

        descriptor.Attributes.Add(new IdentityAttribute(
            "urn:oid:email",
            new[] { IdentityClaimValue.FromString("user@example.com") },
            nameFormat: "uri",
            friendlyName: "email"));
        descriptor.Attributes.Add(new IdentityAttribute(
            "urn:oid:groups",
            new[] { IdentityClaimValue.FromString("staff"), IdentityClaimValue.FromString("admin") },
            nameFormat: "uri"));

        return descriptor;
    }

    private static SamlTokenValidationOptions ConformantOptions()
        => new(now)
        {
            ExpectedIssuer = Issuer,
            ExpectedAudience = RelyingParty,
            ExpectedRecipient = AcsUrl,
            ExpectedInResponseTo = RequestId,
        };

    private static IEnumerable<IReadOnlyList<string>> Audience(string audience)
        => new[] { (IReadOnlyList<string>)new[] { audience } };
}
