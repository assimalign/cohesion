namespace Assimalign.Cohesion.IdentityModel.Token.Saml;

/// <summary>
/// Represents an encrypted SAML element (<c>EncryptedID</c>, <c>EncryptedAssertion</c>, or
/// <c>EncryptedAttribute</c>) the descriptive token layer cannot open. It is a preserved
/// marker, never silently dropped: decryption is a keyed operation deferred to a future
/// Security-layer package, which opens the element and supplies the plaintext.
/// </summary>
public sealed class SamlEncryptedElement
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlEncryptedElement" /> class.
    /// </summary>
    /// <param name="rawXml">The verbatim encrypted-element XML, preserved for a keyed decryptor.</param>
    /// <param name="encryptedDataId">The <c>EncryptedData</c> identifier, when present.</param>
    /// <param name="recipientKeyHint">A hint identifying the recipient key, when present.</param>
    public SamlEncryptedElement(string? rawXml = null, string? encryptedDataId = null, string? recipientKeyHint = null)
    {
        RawXml = rawXml;
        EncryptedDataId = encryptedDataId;
        RecipientKeyHint = recipientKeyHint;
    }

    /// <summary>
    /// Gets the verbatim encrypted-element XML.
    /// </summary>
    public string? RawXml { get; }

    /// <summary>
    /// Gets the <c>EncryptedData</c> identifier, when present.
    /// </summary>
    public string? EncryptedDataId { get; }

    /// <summary>
    /// Gets a hint identifying the recipient key, when present.
    /// </summary>
    public string? RecipientKeyHint { get; }
}
