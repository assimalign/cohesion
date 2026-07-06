using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// The single, pinned recipe for lifting a SAML <see cref="SamlNameId" /> into the canonical
/// <see cref="SubjectIdentifier" />. Both the login leg (assertion subject) and the logout
/// leg (logout request NameID) must derive identifiers through this recipe, or single-logout
/// correlation silently fails — <see cref="SubjectIdentifier" /> equality spans (Value,
/// Format, Issuer, RelyingPartyQualifier). The SAML Core §3.7.3.2 obligation on the identity
/// provider to send a logout NameID consistent with the login NameID is what makes the two
/// legs converge; this recipe applies identical field mapping and qualifier defaulting on
/// both.
/// </summary>
public static class SamlSubjectExtensions
{
    extension(SamlNameId nameId)
    {
        /// <summary>
        /// Derives the canonical subject identifier from the NameID's wire fields: value =
        /// NameID value; format = NameID Format (normalized to unspecified when absent);
        /// issuer = NameQualifier, or <paramref name="issuerFallback" /> when the qualifier
        /// is absent (SAML Core §2.2.3); relying-party qualifier = SPNameQualifier; the
        /// SP-provided identifier rides <see cref="SubjectIdentifier.Properties" />.
        /// </summary>
        /// <param name="issuerFallback">The assertion or message issuer, used as the scope when the NameID omits its name qualifier.</param>
        /// <returns>The canonical identifier.</returns>
        public SubjectIdentifier GetSubjectIdentifier(string? issuerFallback = null)
        {
            IReadOnlyDictionary<string, string>? properties = null;
            if (nameId.SpProvidedId is not null)
            {
                properties = new Dictionary<string, string>(1)
                {
                    ["SPProvidedID"] = nameId.SpProvidedId,
                };
            }

            return new SubjectIdentifier(
                value: nameId.Value,
                format: nameId.Format,
                issuer: nameId.NameQualifier ?? issuerFallback,
                relyingPartyQualifier: nameId.SpNameQualifier,
                properties: properties);
        }
    }
}
