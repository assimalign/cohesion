using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Represents the content of a SAML 2.0 assertion — the SAML branch's token contract, the
/// counterpart to the OpenID Connect ID token. Concrete XML parsing and cryptographic
/// validation (signature, decryption) belong to the SAML token package, which materializes
/// this contract; <see cref="Validate" /> owns the pure data rules of SAML Core and the Web
/// Browser SSO profile.
/// </summary>
/// <remarks>
/// The <see cref="Claims" /> collection is built once at materialization from the subject
/// NameID and the attribute statements, stamped with SAML provenance. Attribute claims keep
/// their <em>raw SAML attribute names</em> (not remapped to canonical types — that is the
/// cross-protocol mapper's job) and carry the original name format, friendly name, and value
/// type in provenance, so nothing is lost. Multi-value and repeated attributes become
/// duplicate claims, per the family's canonical multi-value rule. Authentication-statement
/// data (instant, context, session index) is deliberately not forced into the claim
/// collection — it flows to authorization and session layers through
/// <see cref="AuthenticationContext" />, kept SAML-native.
/// </remarks>
public sealed class SamlAssertion
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlAssertion" /> class by snapshotting
    /// the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The assertion contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="IdentityModelException">
    /// Thrown when the descriptor has no identifier, or a statement list contains a null
    /// entry.
    /// </exception>
    public SamlAssertion(SamlAssertionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (string.IsNullOrWhiteSpace(descriptor.Id))
        {
            throw new IdentityModelException("A SAML assertion requires an identifier.");
        }

        Id = descriptor.Id;
        Version = descriptor.Version;
        IssueInstant = descriptor.IssueInstant;
        Issuer = descriptor.Issuer;
        Subject = descriptor.Subject;
        Conditions = descriptor.Conditions;
        AuthnStatements = SnapshotList(descriptor.AuthnStatements);
        AttributeStatements = SnapshotList(descriptor.AttributeStatements);
        RawXml = descriptor.RawXml;
        Claims = BuildClaims(descriptor);
    }

    /// <summary>
    /// Gets the assertion identifier.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the SAML version.
    /// </summary>
    public string? Version { get; }

    /// <summary>
    /// Gets the instant the assertion was issued.
    /// </summary>
    public DateTimeOffset? IssueInstant { get; }

    /// <summary>
    /// Gets the issuer.
    /// </summary>
    public SamlNameId? Issuer { get; }

    /// <summary>
    /// Gets the subject.
    /// </summary>
    public SamlSubject? Subject { get; }

    /// <summary>
    /// Gets the conditions.
    /// </summary>
    public SamlConditions? Conditions { get; }

    /// <summary>
    /// Gets the authentication statements.
    /// </summary>
    public IReadOnlyList<SamlAuthnStatement> AuthnStatements { get; }

    /// <summary>
    /// Gets the attribute statements.
    /// </summary>
    public IReadOnlyList<SamlAttributeStatement> AttributeStatements { get; }

    /// <summary>
    /// Gets the verbatim, as-received assertion element octets, when retained. This is the
    /// exact <c>&lt;saml:Assertion&gt;</c> subtree — never a re-serialization — so the token
    /// package can re-verify an assertion-level signature independently of any
    /// response-level signature.
    /// </summary>
    public string? RawXml { get; }

    /// <summary>
    /// Gets every claim the assertion asserts, with SAML provenance.
    /// </summary>
    public IIdentityClaimCollection Claims { get; }

    /// <summary>
    /// Validates the assertion's data rules per SAML Core and the Web Browser SSO profile:
    /// issuer match, subject presence, the conditions temporal window and audience
    /// restrictions, an existentially-satisfying bearer subject confirmation, and a present
    /// authentication statement carrying a non-empty authentication context (the bearer and
    /// authentication-statement requirements are the profile posture and are opt-out through
    /// <paramref name="options" /> for the attribute-only assertions SAML Core permits).
    /// Signature and decryption are the token package's concern and are deliberately absent.
    /// </summary>
    /// <param name="options">The validation expectations.</param>
    /// <returns>The validation findings.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options" /> is null.</exception>
    public ProtocolValidationResult Validate(SamlAssertionValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = new List<ProtocolValidationDiagnostic>();

        ValidateIssuer(diagnostics, options);
        ValidateSubject(diagnostics);
        ValidateConditions(diagnostics, options);
        ValidateBearerConfirmation(diagnostics, options);
        ValidateAuthnStatements(diagnostics, options);

        return diagnostics.Count == 0 ? ProtocolValidationResult.Success : new ProtocolValidationResult(diagnostics);
    }

    private void ValidateIssuer(List<ProtocolValidationDiagnostic> diagnostics, SamlAssertionValidationOptions options)
    {
        if (Issuer is null)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.MissingRequiredMember,
                "The assertion has no issuer.",
                member: "Issuer"));
            return;
        }

        if (options.ExpectedIssuer is not null &&
            !string.Equals(Issuer.Value, options.ExpectedIssuer, StringComparison.Ordinal))
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.IssuerMismatch,
                "The assertion issuer does not match the expected issuer.",
                member: "Issuer"));
        }
    }

    private void ValidateSubject(List<ProtocolValidationDiagnostic> diagnostics)
    {
        // A subject with neither a cleartext NameID nor an encrypted identifier nor a
        // confirming NameID has no principal.
        var hasPrincipal = Subject is not null
            && (Subject.NameId is not null
                || Subject.EncryptedId is not null
                || HasConfirmingNameId(Subject));

        if (!hasPrincipal)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                SamlValidationCodes.SubjectMissing,
                "The assertion has no identifiable subject.",
                member: "Subject"));
        }
    }

    private void ValidateConditions(List<ProtocolValidationDiagnostic> diagnostics, SamlAssertionValidationOptions options)
    {
        if (Conditions is null)
        {
            return;
        }

        if (Conditions.NotOnOrAfter is not null &&
            options.ValidateAt - options.ClockSkew >= Conditions.NotOnOrAfter.Value)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.Expired,
                "The assertion conditions are expired.",
                member: "Conditions.NotOnOrAfter"));
        }

        if (Conditions.NotBefore is not null &&
            options.ValidateAt + options.ClockSkew < Conditions.NotBefore.Value)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                ProtocolValidationCodes.NotYetValid,
                "The assertion conditions are not yet valid.",
                member: "Conditions.NotBefore"));
        }

        if (options.ExpectedAudience is not null &&
            Conditions.AudienceRestrictions.Count > 0 &&
            !Conditions.IsAudienceSatisfied(options.ExpectedAudience))
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                SamlValidationCodes.AudienceRestrictionFailed,
                "The relying party does not satisfy the assertion's audience restrictions.",
                member: "Conditions.AudienceRestriction"));
        }
    }

    private void ValidateBearerConfirmation(List<ProtocolValidationDiagnostic> diagnostics, SamlAssertionValidationOptions options)
    {
        if (!options.RequireBearerConfirmation)
        {
            return;
        }

        var satisfied = false;
        if (Subject is not null)
        {
            foreach (var confirmation in Subject.SubjectConfirmations)
            {
                if (confirmation.IsBearer && IsBearerDataSatisfied(confirmation.Data, options))
                {
                    satisfied = true;
                    break;
                }
            }
        }

        if (!satisfied)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                SamlValidationCodes.SubjectConfirmationInvalid,
                "No bearer subject confirmation satisfies the Web Browser SSO profile rules.",
                member: "Subject.SubjectConfirmation"));
        }
    }

    private static bool IsBearerDataSatisfied(SamlSubjectConfirmationData? data, SamlAssertionValidationOptions options)
    {
        // The bearer profile requires NotOnOrAfter present and in the future, forbids
        // NotBefore, and requires Recipient / InResponseTo to match when expected.
        if (data is null || data.NotBefore is not null)
        {
            return false;
        }

        if (data.NotOnOrAfter is null || options.ValidateAt - options.ClockSkew >= data.NotOnOrAfter.Value)
        {
            return false;
        }

        if (options.ExpectedRecipient is not null &&
            !string.Equals(data.Recipient, options.ExpectedRecipient, StringComparison.Ordinal))
        {
            return false;
        }

        // InResponseTo must equal the sent request id; both-null matches IdP-initiated SSO.
        return string.Equals(data.InResponseTo, options.ExpectedInResponseTo, StringComparison.Ordinal);
    }

    private void ValidateAuthnStatements(List<ProtocolValidationDiagnostic> diagnostics, SamlAssertionValidationOptions options)
    {
        // The Web Browser SSO profile (SAML Profiles §4.1.4.2) requires an authentication
        // statement; an assertion with none asserts no authentication context at all, which
        // is the same fail-closed concern as an empty context. Attribute-only assertions
        // (SAML Core §2.7.2) opt out through RequireAuthnStatement.
        if (options.RequireAuthnStatement && AuthnStatements.Count == 0)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                SamlValidationCodes.AuthnContextMissing,
                "The assertion carries no authentication statement.",
                member: "AuthnStatement"));
        }

        foreach (var statement in AuthnStatements)
        {
            if (statement.AuthnContext.IsEmpty)
            {
                diagnostics.Add(new ProtocolValidationDiagnostic(
                    ProtocolValidationSeverity.Error,
                    SamlValidationCodes.AuthnContextMissing,
                    "An authentication statement carries an authentication context with no class or declaration.",
                    member: "AuthnStatement.AuthnContext"));
            }
        }
    }

    private static bool HasConfirmingNameId(SamlSubject subject)
    {
        foreach (var confirmation in subject.SubjectConfirmations)
        {
            if (confirmation.NameId is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<TItem> SnapshotList<TItem>(IList<TItem> source)
        where TItem : class
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Count == 0)
        {
            return Array.Empty<TItem>();
        }

        var snapshot = new TItem[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            snapshot[index] = source[index]
                ?? throw new IdentityModelException("A SAML assertion statement list must not contain null entries.");
        }

        return new ReadOnlyCollection<TItem>(snapshot);
    }

    private static IIdentityClaimCollection BuildClaims(SamlAssertionDescriptor descriptor)
    {
        var issuer = descriptor.Issuer?.Value;
        var claims = new List<IIdentityClaim>();

        if (descriptor.Subject?.NameId is { } nameId)
        {
            // The NameID format is a name FORMAT, not a wire claim name, so it rides
            // OriginalNameFormat — OriginalType is reserved for original wire names, the
            // invariant the cross-protocol mapper's audit trail depends on.
            claims.Add(new IdentityClaim(
                IdentityClaimTypes.Subject,
                nameId.Value,
                issuer,
                new IdentityClaimProvenance(
                    AuthenticationProtocol.Saml2,
                    originalIssuer: issuer,
                    originalNameFormat: nameId.Format)));
        }

        // Attribute claims keep their raw SAML attribute name (the cross-protocol mapper
        // remaps them); provenance carries the name format, friendly name, and value type so
        // nothing is lost. Multi-value and repeated attributes become duplicate claims.
        foreach (var statement in descriptor.AttributeStatements)
        {
            foreach (var attribute in statement.Attributes)
            {
                foreach (var value in attribute.Values)
                {
                    claims.Add(new IdentityClaim(
                        attribute.Name,
                        value,
                        issuer,
                        new IdentityClaimProvenance(
                            AuthenticationProtocol.Saml2,
                            originalType: attribute.Name,
                            originalIssuer: issuer,
                            originalNameFormat: attribute.NameFormat,
                            originalFriendlyName: attribute.FriendlyName)));
                }
            }
        }

        return new IdentityClaimCollection(claims);
    }
}
