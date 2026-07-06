using System;
using System.Collections.Generic;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml.Tests;

/// <summary>
/// Shared builders for a Web Browser SSO profile-conformant SAML assertion and its matching
/// validation options, so each test mutates one field away from conformant and asserts the
/// single resulting diagnostic. Every builder returns a fresh graph — tests never share
/// mutable state.
/// </summary>
internal static class SamlAssertionFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    public const string Issuer = "https://idp.example.com/saml";
    public const string RelyingParty = "https://sp.example.com";
    public const string AcsUrl = "https://sp.example.com/acs";
    public const string RequestId = "_req-9f86d081";
    public const string SubjectValue = "user@example.com";

    /// <summary>
    /// Builds a fully conformant assertion descriptor: a bearer subject confirmation whose
    /// recipient and <c>InResponseTo</c> match, a temporal window that brackets
    /// <see cref="Now" />, an audience restriction naming the relying party, and one
    /// authentication statement with a class reference.
    /// </summary>
    public static SamlAssertionDescriptor ConformantAssertion()
    {
        var descriptor = new SamlAssertionDescriptor
        {
            Id = "_a1b2c3d4e5",
            Version = SamlConstants.Version,
            IssueInstant = Now,
            Issuer = new SamlNameId(Issuer, SamlNameIdFormats.Entity),
            Subject = ConformantSubject(),
            Conditions = new SamlConditions(
                notBefore: Now.AddMinutes(-1),
                notOnOrAfter: Now.AddMinutes(5),
                audienceRestrictions: new[] { (IReadOnlyList<string>)new[] { RelyingParty } }),
        };

        descriptor.AuthnStatements.Add(new SamlAuthnStatement(
            new SamlAuthnContext(SamlAuthnContextClasses.PasswordProtectedTransport),
            authnInstant: Now,
            sessionIndex: "sess-1"));

        descriptor.AttributeStatements.Add(new SamlAttributeStatement(new[]
        {
            new IdentityAttribute(
                "urn:oid:1.2.840.113549.1.9.1",
                new[] { IdentityClaimValue.FromString(SubjectValue) },
                nameFormat: SamlAttributeNameFormats.Uri,
                friendlyName: "email"),
            new IdentityAttribute(
                "urn:oid:1.3.6.1.4.1.5923.1.1.1.1",
                new[]
                {
                    IdentityClaimValue.FromString("staff"),
                    IdentityClaimValue.FromString("admin"),
                },
                nameFormat: SamlAttributeNameFormats.Uri,
                friendlyName: "eduPersonAffiliation"),
        }));

        return descriptor;
    }

    public static SamlSubject ConformantSubject()
        => new(
            new SamlNameId(
                SubjectValue,
                format: SamlNameIdFormats.EmailAddress,
                nameQualifier: Issuer,
                spNameQualifier: RelyingParty),
            new[]
            {
                new SamlSubjectConfirmation(
                    SamlConfirmationMethods.Bearer,
                    data: new SamlSubjectConfirmationData(
                        recipient: AcsUrl,
                        notOnOrAfter: Now.AddMinutes(5),
                        inResponseTo: RequestId)),
            });

    public static SamlAssertionValidationOptions ConformantOptions()
        => new(Now)
        {
            ExpectedIssuer = Issuer,
            ExpectedAudience = RelyingParty,
            ExpectedRecipient = AcsUrl,
            ExpectedInResponseTo = RequestId,
        };
}
