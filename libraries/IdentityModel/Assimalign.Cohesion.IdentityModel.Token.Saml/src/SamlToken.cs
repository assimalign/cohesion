using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token;

namespace Assimalign.Cohesion.IdentityModel.Token.Saml;

/// <summary>
/// Represents an immutable SAML assertion token normalized onto the canonical identity model:
/// the subject is lifted from the NameID through the pinned recipe, attributes become claims with
/// SAML provenance, the authentication statement flows to the base authentication context, and
/// the conditions window projects onto the base temporal members — while the typed SAML structure
/// (NameID, conditions, subject confirmations, encrypted markers) is preserved for fidelity.
/// </summary>
/// <remarks>
/// This is the concrete SAML assertion document layer. It does not read or write SAML XML, verify
/// the assertion signature, or decrypt encrypted elements — those keyed/parse operations are
/// deferred seams; the verbatim <see cref="AssertionXml" /> is preserved for a signature verifier
/// and the <see cref="EncryptedId" />/<see cref="EncryptedAttributes" /> markers for a decryptor.
/// The full SAML Core / Web Browser SSO profile validation is the protocol branch's
/// <c>SamlAssertion.Validate</c>; <see cref="Validate" /> here owns the token substrate (composed
/// neutral rules plus the bearer confirmation-data window), which intentionally overlaps on the
/// temporal window — document substrate versus protocol profile.
/// </remarks>
public sealed class SamlToken : IdentityToken, ISamlToken
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlToken" /> class by snapshotting the
    /// provided descriptor.
    /// </summary>
    /// <param name="descriptor">The SAML token descriptor.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a subject confirmation or encrypted marker entry is null.</exception>
    public SamlToken(SamlTokenDescriptor descriptor)
        : base(IdentityTokenKind.Saml, BuildBase(descriptor))
    {
        AssertionId = descriptor.AssertionId;
        Version = descriptor.Version;
        NameId = descriptor.NameId;
        Conditions = descriptor.Conditions;
        EncryptedId = descriptor.EncryptedId;
        SubjectConfirmations = Snapshot(descriptor.SubjectConfirmations);
        EncryptedAttributes = Snapshot(descriptor.EncryptedAttributes);
    }

    /// <inheritdoc />
    public string? AssertionId { get; }

    /// <inheritdoc />
    public string? Version { get; }

    /// <inheritdoc />
    public SamlNameId? NameId { get; }

    /// <inheritdoc />
    public SamlConditions? Conditions { get; }

    /// <inheritdoc />
    public IReadOnlyList<SamlSubjectConfirmation> SubjectConfirmations { get; }

    /// <inheritdoc />
    public SamlEncryptedElement? EncryptedId { get; }

    /// <inheritdoc />
    public IReadOnlyList<SamlEncryptedElement> EncryptedAttributes { get; }

    /// <inheritdoc />
    public string? AssertionXml => RawData;

    /// <summary>
    /// Validates the token substrate: the neutral issuer and temporal rules, the SAML
    /// audience-restriction rule (AND across restrictions, evaluated through
    /// <see cref="SamlConditions.IsAudienceSatisfied" /> — not the base's flat union), and the
    /// bearer subject-confirmation-data window (freshness, plus recipient / in-response-to
    /// equality when expected). It does not verify the signature or run the full SAML Core
    /// profile.
    /// </summary>
    /// <param name="options">The validation expectations.</param>
    /// <returns>The validation findings.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options" /> is null.</exception>
    public TokenValidationResult Validate(SamlTokenValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = new List<TokenValidationDiagnostic>();

        // Compose the neutral base for issuer and temporal only. The base audience check is a
        // flat OR over the (lossy) union projection; SAML needs the AND-across rule, done below.
        var baseResult = base.Validate(new IdentityTokenValidationOptions(options.ValidateAt)
        {
            ClockSkew = options.ClockSkew,
            ExpectedIssuer = options.ExpectedIssuer,
        });
        diagnostics.AddRange(baseResult.Diagnostics);

        ValidateAudience(diagnostics, options);
        ValidateBearerConfirmation(diagnostics, options);

        return diagnostics.Count == 0 ? TokenValidationResult.Success : new TokenValidationResult(diagnostics);
    }

    private void ValidateAudience(List<TokenValidationDiagnostic> diagnostics, SamlTokenValidationOptions options)
    {
        // No expected audience, or an unrestricted assertion (no AudienceRestriction), is
        // satisfied. Otherwise every restriction must be satisfied (AND across).
        if (options.ExpectedAudience is null ||
            Conditions is null ||
            Conditions.AudienceRestrictions.Count == 0)
        {
            return;
        }

        if (!Conditions.IsAudienceSatisfied(options.ExpectedAudience))
        {
            diagnostics.Add(new TokenValidationDiagnostic(
                TokenValidationSeverity.Error,
                TokenValidationCodes.AudienceMismatch,
                "The relying party does not satisfy the assertion's audience restrictions.",
                member: nameof(Conditions)));
        }
    }

    private void ValidateBearerConfirmation(List<TokenValidationDiagnostic> diagnostics, SamlTokenValidationOptions options)
    {
        var hasBearer = false;
        var satisfied = false;

        foreach (var confirmation in SubjectConfirmations)
        {
            if (!confirmation.IsBearer)
            {
                continue;
            }

            hasBearer = true;
            if (IsBearerDataSatisfied(confirmation.Data, options))
            {
                satisfied = true;
                break;
            }
        }

        // A present-but-stale bearer confirmation is always a finding; an absent one is a finding
        // only when the caller opts into the profile posture.
        if ((hasBearer && !satisfied) || (!hasBearer && options.RequireBearerConfirmation))
        {
            diagnostics.Add(new TokenValidationDiagnostic(
                TokenValidationSeverity.Error,
                SamlTokenValidationCodes.SubjectConfirmationInvalid,
                hasBearer
                    ? "No bearer subject confirmation satisfies the confirmation-data window."
                    : "The token carries no bearer subject confirmation.",
                member: nameof(SubjectConfirmations)));
        }
    }

    private static bool IsBearerDataSatisfied(SamlSubjectConfirmationData? data, SamlTokenValidationOptions options)
    {
        // The bearer window requires NotOnOrAfter present and in the future, forbids NotBefore,
        // and matches Recipient / InResponseTo when the caller expects them.
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

        return options.ExpectedInResponseTo is null ||
            string.Equals(data.InResponseTo, options.ExpectedInResponseTo, StringComparison.Ordinal);
    }

    private static IReadOnlyList<TItem> Snapshot<TItem>(IList<TItem> source)
        where TItem : class
    {
        if (source.Count == 0)
        {
            return Array.Empty<TItem>();
        }

        var snapshot = new TItem[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            snapshot[index] = source[index]
                ?? throw new ArgumentException("A SAML token list must not contain null entries.", nameof(source));
        }

        return new ReadOnlyCollection<TItem>(snapshot);
    }

    private static IdentityTokenDescriptor BuildBase(SamlTokenDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        // A fresh descriptor derived from the typed SAML structure — the caller's descriptor is
        // never mutated. The base ctor reads the normalized surface from this.
        var merged = new MergedDescriptor
        {
            Protocol = AuthenticationProtocol.Saml2,
            Id = descriptor.AssertionId,
            Issuer = descriptor.Issuer,
            IssuedAt = descriptor.IssuedAt,
            RawData = descriptor.RawData,
            AuthenticationContext = descriptor.AuthenticationContext,
        };

        if (descriptor.NameId is { } nameId)
        {
            merged.Subject = nameId.GetSubjectIdentifier(descriptor.Issuer);
        }

        if (descriptor.Conditions is { } conditions)
        {
            merged.NotBefore = conditions.NotBefore;
            merged.ExpiresAt = conditions.NotOnOrAfter;

            // Base Audiences is a lossy union convenience — the authoritative audience surface is
            // SamlConditions (AND-across). De-duplicate so the projection stays tidy.
            foreach (var restriction in conditions.AudienceRestrictions)
            {
                foreach (var audience in restriction)
                {
                    if (!merged.Audiences.Contains(audience))
                    {
                        merged.Audiences.Add(audience);
                    }
                }
            }
        }

        BuildClaims(descriptor, merged);

        foreach (var (name, value) in descriptor.Properties)
        {
            merged.Properties[name] = value;
        }

        return merged;
    }

    private static void BuildClaims(SamlTokenDescriptor descriptor, IdentityTokenDescriptor merged)
    {
        var issuer = descriptor.Issuer;

        // sub from the NameID; one claim per attribute value keyed by the RAW SAML name with
        // saml2 provenance — the same rule the protocol branch's SamlAssertion.BuildClaims uses,
        // so a JWT- and a SAML-normalized principal resolve to the same canonical shape.
        if (descriptor.NameId is { } nameId)
        {
            // The NameID format rides OriginalNameFormat, not OriginalType — OriginalType is
            // reserved for original wire names (mirrors SamlAssertion.BuildClaims).
            merged.Claims.Add(new IdentityClaim(
                IdentityClaimTypes.Subject,
                nameId.Value,
                issuer,
                new IdentityClaimProvenance(
                    AuthenticationProtocol.Saml2,
                    originalIssuer: issuer,
                    originalNameFormat: nameId.Format)));
        }

        foreach (var attribute in descriptor.Attributes)
        {
            foreach (var value in attribute.Values)
            {
                merged.Claims.Add(new IdentityClaim(
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

    private sealed class MergedDescriptor : IdentityTokenDescriptor
    {
    }
}
