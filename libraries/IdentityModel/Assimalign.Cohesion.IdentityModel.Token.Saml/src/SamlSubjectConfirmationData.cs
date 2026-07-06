using System;

namespace Assimalign.Cohesion.IdentityModel.Token.Saml;

/// <summary>
/// Represents a SAML 2.0 <c>SubjectConfirmationData</c> (SAML Core §2.4.1.2): the data that
/// constrains how and where a subject confirmation is valid. The token package validates the
/// freshness of this window (a document-substrate concern); it does not verify holder-of-key
/// cryptography.
/// </summary>
public sealed class SamlSubjectConfirmationData
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlSubjectConfirmationData" /> class.
    /// </summary>
    /// <param name="recipient">The URI the confirmation is addressed to (the assertion consumer URL).</param>
    /// <param name="notBefore">The instant before which the confirmation is not valid. The bearer profile forbids this.</param>
    /// <param name="notOnOrAfter">The instant at or after which the confirmation is not valid.</param>
    /// <param name="inResponseTo">The identifier of the request this confirmation answers.</param>
    /// <param name="address">The network address the subject is confirmed from.</param>
    /// <param name="keyInfoXml">The verbatim <c>KeyInfo</c> XML for holder-of-key confirmations, when present.</param>
    public SamlSubjectConfirmationData(
        string? recipient = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notOnOrAfter = null,
        string? inResponseTo = null,
        string? address = null,
        string? keyInfoXml = null)
    {
        Recipient = recipient;
        NotBefore = notBefore;
        NotOnOrAfter = notOnOrAfter;
        InResponseTo = inResponseTo;
        Address = address;
        KeyInfoXml = keyInfoXml;
    }

    /// <summary>
    /// Gets the URI the confirmation is addressed to.
    /// </summary>
    public string? Recipient { get; }

    /// <summary>
    /// Gets the instant before which the confirmation is not valid.
    /// </summary>
    public DateTimeOffset? NotBefore { get; }

    /// <summary>
    /// Gets the instant at or after which the confirmation is not valid.
    /// </summary>
    public DateTimeOffset? NotOnOrAfter { get; }

    /// <summary>
    /// Gets the identifier of the request this confirmation answers.
    /// </summary>
    public string? InResponseTo { get; }

    /// <summary>
    /// Gets the network address the subject is confirmed from.
    /// </summary>
    public string? Address { get; }

    /// <summary>
    /// Gets the verbatim <c>KeyInfo</c> XML for holder-of-key confirmations. Cryptographic use
    /// is a deferred keyed seam; this preserves the reference descriptively.
    /// </summary>
    public string? KeyInfoXml { get; }
}
