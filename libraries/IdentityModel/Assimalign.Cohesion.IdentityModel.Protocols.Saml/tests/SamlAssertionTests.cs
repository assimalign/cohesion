using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml.Tests;

/// <summary>
/// Verifies the SAML assertion data rules (SAML Core and the Web Browser SSO profile) and the
/// claim projection that materialization builds from the subject and attribute statements.
/// </summary>
public sealed class SamlAssertionTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A conformant assertion should validate")]
    public void Validate_WhenAssertionConformant_ShouldSucceed()
    {
        var assertion = new SamlAssertion(SamlAssertionFixtures.ConformantAssertion());

        var result = assertion.Validate(SamlAssertionFixtures.ConformantOptions());

        result.Succeeded.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: An expired assertion should fail on conditions")]
    public void Validate_WhenConditionsExpired_ShouldReportExpired()
    {
        var descriptor = SamlAssertionFixtures.ConformantAssertion();
        descriptor.Conditions = new SamlConditions(
            notBefore: SamlAssertionFixtures.Now.AddMinutes(-10),
            notOnOrAfter: SamlAssertionFixtures.Now.AddMinutes(-5),
            audienceRestrictions: new[] { (IReadOnlyList<string>)new[] { SamlAssertionFixtures.RelyingParty } });
        var assertion = new SamlAssertion(descriptor);

        var result = assertion.Validate(SamlAssertionFixtures.ConformantOptions());

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == ProtocolValidationCodes.Expired);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: Clock skew should keep a just-expired assertion valid")]
    public void Validate_WhenExpiredWithinClockSkew_ShouldSucceed()
    {
        var descriptor = SamlAssertionFixtures.ConformantAssertion();
        // Conditions expired one minute ago; the confirmation likewise. A five-minute skew
        // must forgive both, since the skew is applied to the caller instant, not per-field.
        descriptor.Conditions = new SamlConditions(
            notBefore: SamlAssertionFixtures.Now.AddMinutes(-10),
            notOnOrAfter: SamlAssertionFixtures.Now.AddMinutes(-1),
            audienceRestrictions: new[] { (IReadOnlyList<string>)new[] { SamlAssertionFixtures.RelyingParty } });
        descriptor.Subject = new SamlSubject(
            new SamlNameId(SamlAssertionFixtures.SubjectValue, SamlNameIdFormats.EmailAddress),
            new[]
            {
                new SamlSubjectConfirmation(
                    SamlConfirmationMethods.Bearer,
                    data: new SamlSubjectConfirmationData(
                        recipient: SamlAssertionFixtures.AcsUrl,
                        notOnOrAfter: SamlAssertionFixtures.Now.AddMinutes(-1),
                        inResponseTo: SamlAssertionFixtures.RequestId)),
            });
        var assertion = new SamlAssertion(descriptor);

        var options = SamlAssertionFixtures.ConformantOptions();
        options.ClockSkew = TimeSpan.FromMinutes(5);

        assertion.Validate(options).Succeeded.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A wrong issuer should fail")]
    public void Validate_WhenIssuerMismatched_ShouldReportIssuerMismatch()
    {
        var assertion = new SamlAssertion(SamlAssertionFixtures.ConformantAssertion());

        var options = SamlAssertionFixtures.ConformantOptions();
        options.ExpectedIssuer = "https://attacker.example.com";

        var result = assertion.Validate(options);

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == ProtocolValidationCodes.IssuerMismatch);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: An unsatisfied audience should fail closed")]
    public void Validate_WhenRelyingPartyNotInAudience_ShouldReportAudienceRestrictionFailed()
    {
        var assertion = new SamlAssertion(SamlAssertionFixtures.ConformantAssertion());

        var options = SamlAssertionFixtures.ConformantOptions();
        options.ExpectedAudience = "https://other-sp.example.com";

        var result = assertion.Validate(options);

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == SamlValidationCodes.AudienceRestrictionFailed);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A missing bearer confirmation should fail")]
    public void Validate_WhenNoBearerConfirmation_ShouldReportSubjectConfirmationInvalid()
    {
        var descriptor = SamlAssertionFixtures.ConformantAssertion();
        // Holder-of-key only: no bearer confirmation, so the Web Browser SSO profile is unmet.
        descriptor.Subject = new SamlSubject(
            new SamlNameId(SamlAssertionFixtures.SubjectValue, SamlNameIdFormats.EmailAddress),
            new[] { new SamlSubjectConfirmation(SamlConfirmationMethods.HolderOfKey) });
        var assertion = new SamlAssertion(descriptor);

        var result = assertion.Validate(SamlAssertionFixtures.ConformantOptions());

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == SamlValidationCodes.SubjectConfirmationInvalid);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A recipient mismatch should fail the bearer confirmation")]
    public void Validate_WhenRecipientMismatched_ShouldReportSubjectConfirmationInvalid()
    {
        var assertion = new SamlAssertion(SamlAssertionFixtures.ConformantAssertion());

        var options = SamlAssertionFixtures.ConformantOptions();
        options.ExpectedRecipient = "https://sp.example.com/other-acs";

        var result = assertion.Validate(options);

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == SamlValidationCodes.SubjectConfirmationInvalid);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: An empty authentication context should fail")]
    public void Validate_WhenAuthnContextEmpty_ShouldReportAuthnContextMissing()
    {
        var descriptor = SamlAssertionFixtures.ConformantAssertion();
        descriptor.AuthnStatements.Clear();
        descriptor.AuthnStatements.Add(new SamlAuthnStatement(new SamlAuthnContext()));
        var assertion = new SamlAssertion(descriptor);

        var result = assertion.Validate(SamlAssertionFixtures.ConformantOptions());

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == SamlValidationCodes.AuthnContextMissing);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: An assertion with no authentication statement should fail by default")]
    public void Validate_WhenNoAuthnStatement_ShouldReportAuthnContextMissing()
    {
        var descriptor = SamlAssertionFixtures.ConformantAssertion();
        descriptor.AuthnStatements.Clear();
        var assertion = new SamlAssertion(descriptor);

        var result = assertion.Validate(SamlAssertionFixtures.ConformantOptions());

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == SamlValidationCodes.AuthnContextMissing);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: An attribute-only assertion should validate when the statement is not required")]
    public void Validate_WhenNoAuthnStatementButOptedOut_ShouldSucceed()
    {
        // SAML Core §2.7.2 permits attribute-only assertions with no authentication statement.
        var descriptor = SamlAssertionFixtures.ConformantAssertion();
        descriptor.AuthnStatements.Clear();
        var assertion = new SamlAssertion(descriptor);

        var options = SamlAssertionFixtures.ConformantOptions();
        options.RequireAuthnStatement = false;

        assertion.Validate(options).Succeeded.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A subjectless assertion should fail")]
    public void Validate_WhenSubjectHasNoPrincipal_ShouldReportSubjectMissing()
    {
        var descriptor = SamlAssertionFixtures.ConformantAssertion();
        descriptor.Subject = new SamlSubject();
        var assertion = new SamlAssertion(descriptor);

        var result = assertion.Validate(SamlAssertionFixtures.ConformantOptions());

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == SamlValidationCodes.SubjectMissing);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: An IdP-initiated assertion should validate with no InResponseTo")]
    public void Validate_WhenIdpInitiated_ShouldSucceedWithoutInResponseTo()
    {
        var descriptor = SamlAssertionFixtures.ConformantAssertion();
        descriptor.Subject = new SamlSubject(
            new SamlNameId(SamlAssertionFixtures.SubjectValue, SamlNameIdFormats.EmailAddress),
            new[]
            {
                new SamlSubjectConfirmation(
                    SamlConfirmationMethods.Bearer,
                    data: new SamlSubjectConfirmationData(
                        recipient: SamlAssertionFixtures.AcsUrl,
                        notOnOrAfter: SamlAssertionFixtures.Now.AddMinutes(5))),
            });
        var assertion = new SamlAssertion(descriptor);

        var options = SamlAssertionFixtures.ConformantOptions();
        options.ExpectedInResponseTo = null;

        assertion.Validate(options).Succeeded.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: An assertion requires an identifier")]
    public void Construct_WhenNoId_ShouldThrow()
    {
        var descriptor = SamlAssertionFixtures.ConformantAssertion();
        descriptor.Id = null;

        Should.Throw<IdentityModelException>(() => new SamlAssertion(descriptor));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: Claims should carry the subject from the NameID")]
    public void Claims_WhenBuilt_ShouldCarrySubjectFromNameId()
    {
        var assertion = new SamlAssertion(SamlAssertionFixtures.ConformantAssertion());

        assertion.Claims.TryGet(IdentityClaimTypes.Subject, out var subject).ShouldBeTrue();
        subject!.Value.ToString().ShouldBe(SamlAssertionFixtures.SubjectValue);
        subject.Provenance!.Protocol.ShouldBe(AuthenticationProtocol.Saml2);
        subject.Provenance.OriginalType.ShouldBe(SamlNameIdFormats.EmailAddress);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: Claims should keep raw attribute names with provenance")]
    public void Claims_WhenBuilt_ShouldKeepRawAttributeNameAndProvenance()
    {
        var assertion = new SamlAssertion(SamlAssertionFixtures.ConformantAssertion());

        assertion.Claims.TryGet("urn:oid:1.2.840.113549.1.9.1", out var email).ShouldBeTrue();
        email!.Value.ToString().ShouldBe(SamlAssertionFixtures.SubjectValue);
        email.Provenance!.OriginalNameFormat.ShouldBe(SamlAttributeNameFormats.Uri);
        email.Provenance.OriginalFriendlyName.ShouldBe("email");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A multi-value attribute should become duplicate claims")]
    public void Claims_WhenAttributeMultiValued_ShouldBecomeDuplicateClaims()
    {
        var assertion = new SamlAssertion(SamlAssertionFixtures.ConformantAssertion());

        var affiliations = assertion.Claims
            .GetAll("urn:oid:1.3.6.1.4.1.5923.1.1.1.1")
            .Select(claim => claim.Value.ToString())
            .ToArray();

        affiliations.ShouldBe(new[] { "staff", "admin" });
    }
}
