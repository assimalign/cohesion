using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Represents a SAML 2.0 <c>AttributeStatement</c> (SAML Core §2.7.3): the attributes
/// asserted about the subject. Attributes reuse the canonical
/// <see cref="IdentityAttribute" />, which models exactly what a SAML attribute needs — a
/// name, a name format, a friendly name, typed multi-values, and provenance.
/// </summary>
public sealed class SamlAttributeStatement
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlAttributeStatement" /> class.
    /// </summary>
    /// <param name="attributes">The asserted attributes. The sequence is copied.</param>
    /// <param name="encryptedAttributes">The encrypted attribute markers the descriptive layer cannot open. The sequence is copied.</param>
    /// <exception cref="ArgumentNullException">Thrown when an attribute entry is null.</exception>
    public SamlAttributeStatement(
        IEnumerable<IdentityAttribute> attributes,
        IEnumerable<SamlEncryptedElement>? encryptedAttributes = null)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        var attributeSnapshot = new List<IdentityAttribute>();
        foreach (var attribute in attributes)
        {
            ArgumentNullException.ThrowIfNull(attribute, nameof(attributes));
            attributeSnapshot.Add(attribute);
        }

        Attributes = attributeSnapshot.ToArray();

        if (encryptedAttributes is null)
        {
            EncryptedAttributes = Array.Empty<SamlEncryptedElement>();
        }
        else
        {
            var encryptedSnapshot = new List<SamlEncryptedElement>();
            foreach (var encrypted in encryptedAttributes)
            {
                ArgumentNullException.ThrowIfNull(encrypted, nameof(encryptedAttributes));
                encryptedSnapshot.Add(encrypted);
            }

            EncryptedAttributes = encryptedSnapshot.ToArray();
        }
    }

    /// <summary>
    /// Gets the asserted attributes.
    /// </summary>
    public IReadOnlyList<IdentityAttribute> Attributes { get; }

    /// <summary>
    /// Gets the encrypted attribute markers the descriptive layer cannot open.
    /// </summary>
    public IReadOnlyList<SamlEncryptedElement> EncryptedAttributes { get; }
}
