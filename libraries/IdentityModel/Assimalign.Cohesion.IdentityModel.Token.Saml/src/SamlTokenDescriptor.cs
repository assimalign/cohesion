using System.Collections.Generic;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token;

namespace Assimalign.Cohesion.IdentityModel.Token.Saml;

/// <summary>
/// Describes the contents of a SAML assertion token before it is materialized into an immutable
/// <see cref="SamlToken" />. The author sets the typed SAML structure (NameID, conditions,
/// subject confirmations, attributes, authentication context); materialization derives the
/// normalized base surface (subject, claims, temporal window, audiences) from it.
/// </summary>
/// <remarks>
/// Set the assertion issuer through the inherited <see cref="IdentityTokenDescriptor.Issuer" />
/// and the authentication statement through the inherited
/// <see cref="IdentityTokenDescriptor.AuthenticationContext" /> (a SAML AuthnStatement maps onto
/// the root <see cref="AuthenticationContext" /> — instant, class, session indexes, session
/// expiry, and subject locality — so no parallel authentication-statement type is minted). Do
/// not set the base <c>Subject</c>/<c>Claims</c>/<c>Audiences</c>/temporal members directly;
/// materialization overrides them from the typed structure.
/// </remarks>
public sealed class SamlTokenDescriptor : IdentityTokenDescriptor
{
    /// <summary>
    /// Gets or sets the assertion identifier. Projected onto the base
    /// <see cref="IdentityTokenDescriptor.Id" />.
    /// </summary>
    public string? AssertionId { get; set; }

    /// <summary>
    /// Gets or sets the SAML assertion version.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the subject NameID. Lifted to the base subject through the pinned recipe.
    /// </summary>
    public SamlNameId? NameId { get; set; }

    /// <summary>
    /// Gets or sets the assertion conditions. The authoritative audience surface; the temporal
    /// window projects onto the base <c>NotBefore</c>/<c>ExpiresAt</c>.
    /// </summary>
    public SamlConditions? Conditions { get; set; }

    /// <summary>
    /// Gets the subject confirmations.
    /// </summary>
    public IList<SamlSubjectConfirmation> SubjectConfirmations { get; } = new List<SamlSubjectConfirmation>();

    /// <summary>
    /// Gets the asserted attributes. Each value becomes a normalized claim keyed by the raw SAML
    /// attribute name with SAML provenance.
    /// </summary>
    public IList<IdentityAttribute> Attributes { get; } = new List<IdentityAttribute>();

    /// <summary>
    /// Gets or sets the encrypted subject identifier marker, when the subject's NameID is
    /// encrypted. Preserved for a keyed decryptor; never silently dropped.
    /// </summary>
    public SamlEncryptedElement? EncryptedId { get; set; }

    /// <summary>
    /// Gets the encrypted attribute markers the descriptive layer cannot open.
    /// </summary>
    public IList<SamlEncryptedElement> EncryptedAttributes { get; } = new List<SamlEncryptedElement>();

    /// <summary>
    /// Gets or sets the original SAML assertion XML. Alias of the base
    /// <see cref="IdentityTokenDescriptor.RawData" />, preserved for signature re-verification.
    /// </summary>
    public string? AssertionXml
    {
        get => RawData;
        set => RawData = value;
    }
}
