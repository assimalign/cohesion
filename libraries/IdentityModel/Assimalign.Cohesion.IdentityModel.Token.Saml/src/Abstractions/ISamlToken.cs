using System.Collections.Generic;

using Assimalign.Cohesion.IdentityModel.Token;

namespace Assimalign.Cohesion.IdentityModel.Token.Saml;

/// <summary>
/// Represents a SAML assertion token: a normalized identity token with the typed SAML assertion
/// structure downstream services need. The <c>object?</c> condition bag the earlier shape carried
/// is replaced by the typed <see cref="SamlConditions" />; the SAML token surface is now
/// <c>object?</c>-free.
/// </summary>
public interface ISamlToken : IIdentityToken
{
    /// <summary>
    /// Gets the assertion identifier.
    /// </summary>
    string? AssertionId { get; }

    /// <summary>
    /// Gets the SAML assertion version.
    /// </summary>
    string? Version { get; }

    /// <summary>
    /// Gets the subject NameID, preserved with wire fidelity.
    /// </summary>
    SamlNameId? NameId { get; }

    /// <summary>
    /// Gets the assertion conditions (the authoritative audience surface and temporal window).
    /// </summary>
    SamlConditions? Conditions { get; }

    /// <summary>
    /// Gets the subject confirmations.
    /// </summary>
    IReadOnlyList<SamlSubjectConfirmation> SubjectConfirmations { get; }

    /// <summary>
    /// Gets the encrypted subject identifier marker, when the subject's NameID is encrypted.
    /// </summary>
    SamlEncryptedElement? EncryptedId { get; }

    /// <summary>
    /// Gets the encrypted attribute markers the descriptive layer cannot open.
    /// </summary>
    IReadOnlyList<SamlEncryptedElement> EncryptedAttributes { get; }

    /// <summary>
    /// Gets the original SAML assertion XML, preserved for signature re-verification.
    /// </summary>
    string? AssertionXml { get; }
}
