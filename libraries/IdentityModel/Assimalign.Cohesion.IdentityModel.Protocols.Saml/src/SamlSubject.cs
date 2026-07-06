using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Represents a SAML 2.0 <c>Subject</c> (SAML Core §2.4.1): the principal an assertion is
/// about, together with the confirmations by which a relying party confirms it. A subject
/// may legally carry no NameID (confirmation-only), or an encrypted identifier the
/// descriptive layer cannot open.
/// </summary>
public sealed class SamlSubject
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlSubject" /> class.
    /// </summary>
    /// <param name="nameId">The subject NameID, when present in the clear.</param>
    /// <param name="subjectConfirmations">The subject confirmations. The sequence is copied.</param>
    /// <param name="encryptedId">The encrypted identifier marker, when the subject's NameID is encrypted.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="subjectConfirmations" /> or an entry is null.</exception>
    public SamlSubject(
        SamlNameId? nameId = null,
        IEnumerable<SamlSubjectConfirmation>? subjectConfirmations = null,
        SamlEncryptedElement? encryptedId = null)
    {
        NameId = nameId;
        EncryptedId = encryptedId;

        if (subjectConfirmations is null)
        {
            SubjectConfirmations = Array.Empty<SamlSubjectConfirmation>();
        }
        else
        {
            var snapshot = new List<SamlSubjectConfirmation>();
            foreach (var confirmation in subjectConfirmations)
            {
                ArgumentNullException.ThrowIfNull(confirmation, nameof(subjectConfirmations));
                snapshot.Add(confirmation);
            }

            SubjectConfirmations = snapshot.ToArray();
        }
    }

    /// <summary>
    /// Gets the subject NameID, when present in the clear.
    /// </summary>
    public SamlNameId? NameId { get; }

    /// <summary>
    /// Gets the subject confirmations.
    /// </summary>
    public IReadOnlyList<SamlSubjectConfirmation> SubjectConfirmations { get; }

    /// <summary>
    /// Gets the encrypted identifier marker, when the subject's NameID is encrypted. The
    /// token package decrypts it and supplies the plaintext <see cref="NameId" />.
    /// </summary>
    public SamlEncryptedElement? EncryptedId { get; }
}
