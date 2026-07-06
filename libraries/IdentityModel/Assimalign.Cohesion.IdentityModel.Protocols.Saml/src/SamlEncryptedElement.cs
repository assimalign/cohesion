using System;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Represents an encrypted SAML element (an <c>EncryptedAssertion</c> or <c>EncryptedID</c>)
/// that a descriptive, non-cryptographic layer cannot open. It preserves the element's
/// verbatim XML so the SAML token package can decrypt it, re-materialize the plaintext
/// contract, and re-verify signatures against the original octets.
/// </summary>
public sealed class SamlEncryptedElement
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlEncryptedElement" /> class.
    /// </summary>
    /// <param name="rawXml">The verbatim, as-received XML of the encrypted element.</param>
    /// <param name="encryptedDataId">The <c>EncryptedData</c> Id, when present.</param>
    /// <param name="recipientKeyHint">A hint identifying the recipient key (for example a key name or thumbprint), when present.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="rawXml" /> is null or whitespace.</exception>
    public SamlEncryptedElement(string rawXml, string? encryptedDataId = null, string? recipientKeyHint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawXml);

        RawXml = rawXml;
        EncryptedDataId = encryptedDataId;
        RecipientKeyHint = recipientKeyHint;
    }

    /// <summary>
    /// Gets the verbatim, as-received XML of the encrypted element.
    /// </summary>
    public string RawXml { get; }

    /// <summary>
    /// Gets the <c>EncryptedData</c> Id, when present.
    /// </summary>
    public string? EncryptedDataId { get; }

    /// <summary>
    /// Gets a hint identifying the recipient key, when present.
    /// </summary>
    public string? RecipientKeyHint { get; }
}
