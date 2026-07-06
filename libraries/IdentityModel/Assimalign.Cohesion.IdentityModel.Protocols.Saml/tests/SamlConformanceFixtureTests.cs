using System.Collections.Generic;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel.Protocols;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml.Tests;

/// <summary>
/// A SAML Web Browser SSO profile conformance matrix (SAML Core §2/§3, SAML Profiles §4.1.4).
/// Each negative fixture starts from the one conformant assertion and violates exactly one
/// profile rule, so the mapping from "which rule was broken" to "which diagnostic code fires"
/// is pinned. The positive fixture proves the conformant assertion carries no findings at all.
/// </summary>
public sealed class SamlConformanceFixtureTests
{
    /// <summary>Identifies a single profile rule to violate.</summary>
    public enum Violation
    {
        ConditionsExpired,
        ConditionsNotYetValid,
        AudienceNotSatisfied,
        NoBearerConfirmation,
        BearerConfirmationExpired,
        NoSubjectPrincipal,
        EmptyAuthnContext,
        NoAuthnStatement,
        WrongIssuer,
    }

    public static TheoryData<Violation, string> Fixtures => new()
    {
        { Violation.ConditionsExpired, ProtocolValidationCodes.Expired },
        { Violation.ConditionsNotYetValid, ProtocolValidationCodes.NotYetValid },
        { Violation.AudienceNotSatisfied, SamlValidationCodes.AudienceRestrictionFailed },
        { Violation.NoBearerConfirmation, SamlValidationCodes.SubjectConfirmationInvalid },
        { Violation.BearerConfirmationExpired, SamlValidationCodes.SubjectConfirmationInvalid },
        { Violation.NoSubjectPrincipal, SamlValidationCodes.SubjectMissing },
        { Violation.EmptyAuthnContext, SamlValidationCodes.AuthnContextMissing },
        { Violation.NoAuthnStatement, SamlValidationCodes.AuthnContextMissing },
        { Violation.WrongIssuer, ProtocolValidationCodes.IssuerMismatch },
    };

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML Conformance: The reference assertion should carry no findings")]
    public void ReferenceAssertion_WhenValidated_ShouldCarryNoDiagnostics()
    {
        var assertion = new SamlAssertion(SamlAssertionFixtures.ConformantAssertion());

        var result = assertion.Validate(SamlAssertionFixtures.ConformantOptions());

        result.Succeeded.ShouldBeTrue();
        result.Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [IdentityModel] - SAML Conformance: Each profile violation should raise its diagnostic")]
    [MemberData(nameof(Fixtures))]
    public void ProfileViolation_WhenValidated_ShouldRaiseExpectedDiagnostic(Violation violation, string expectedCode)
    {
        var descriptor = SamlAssertionFixtures.ConformantAssertion();
        var options = SamlAssertionFixtures.ConformantOptions();
        ApplyViolation(violation, descriptor, options);

        var result = new SamlAssertion(descriptor).Validate(options);

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == expectedCode);
    }

    private static void ApplyViolation(
        Violation violation,
        SamlAssertionDescriptor descriptor,
        SamlAssertionValidationOptions options)
    {
        switch (violation)
        {
            case Violation.ConditionsExpired:
                descriptor.Conditions = new SamlConditions(
                    notOnOrAfter: SamlAssertionFixtures.Now.AddMinutes(-10),
                    audienceRestrictions: Audience());
                break;

            case Violation.ConditionsNotYetValid:
                descriptor.Conditions = new SamlConditions(
                    notBefore: SamlAssertionFixtures.Now.AddMinutes(10),
                    notOnOrAfter: SamlAssertionFixtures.Now.AddMinutes(20),
                    audienceRestrictions: Audience());
                break;

            case Violation.AudienceNotSatisfied:
                options.ExpectedAudience = "https://not-the-audience.example.com";
                break;

            case Violation.NoBearerConfirmation:
                descriptor.Subject = new SamlSubject(
                    new SamlNameId(SamlAssertionFixtures.SubjectValue, SamlNameIdFormats.EmailAddress),
                    new[] { new SamlSubjectConfirmation(SamlConfirmationMethods.HolderOfKey) });
                break;

            case Violation.BearerConfirmationExpired:
                descriptor.Subject = new SamlSubject(
                    new SamlNameId(SamlAssertionFixtures.SubjectValue, SamlNameIdFormats.EmailAddress),
                    new[]
                    {
                        new SamlSubjectConfirmation(
                            SamlConfirmationMethods.Bearer,
                            data: new SamlSubjectConfirmationData(
                                recipient: SamlAssertionFixtures.AcsUrl,
                                notOnOrAfter: SamlAssertionFixtures.Now.AddMinutes(-10),
                                inResponseTo: SamlAssertionFixtures.RequestId)),
                    });
                break;

            case Violation.NoSubjectPrincipal:
                descriptor.Subject = new SamlSubject();
                break;

            case Violation.EmptyAuthnContext:
                descriptor.AuthnStatements.Clear();
                descriptor.AuthnStatements.Add(new SamlAuthnStatement(new SamlAuthnContext()));
                break;

            case Violation.NoAuthnStatement:
                descriptor.AuthnStatements.Clear();
                break;

            case Violation.WrongIssuer:
                options.ExpectedIssuer = "https://attacker.example.com";
                break;
        }
    }

    private static IReadOnlyList<IReadOnlyList<string>> Audience()
        => new[] { (IReadOnlyList<string>)new[] { SamlAssertionFixtures.RelyingParty } };
}
