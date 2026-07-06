using System.Collections.Generic;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Token.Saml;

/// <summary>
/// The pinned recipe for lifting a SAML <see cref="SamlNameId" /> into the canonical
/// <see cref="SubjectIdentifier" />, re-minted for the token branch.
/// </summary>
/// <remarks>
/// This reproduces, field for field, the protocol branch's
/// <c>Assimalign.Cohesion.IdentityModel.Protocols.Saml.SamlSubjectExtensions.GetSubjectIdentifier</c>
/// — the token branch cannot reference the protocol branch (branch independence). The mapping is
/// load-bearing: <see cref="SubjectIdentifier" /> equality spans (Value, Format, Issuer,
/// RelyingPartyQualifier), so a SAML token's subject and the protocol branch's assertion subject
/// must lift identically or single-logout correlation silently breaks. A root-tests drift guard
/// pins the two recipes' output equal.
/// </remarks>
public static class SamlSubjectExtensions
{
    extension(SamlNameId nameId)
    {
        /// <summary>
        /// Derives the canonical subject identifier from the NameID's wire fields: value =
        /// NameID value; format = NameID format; issuer = NameQualifier, or
        /// <paramref name="issuerFallback" /> when the qualifier is absent (SAML Core §2.2.3);
        /// relying-party qualifier = SPNameQualifier; the SP-provided identifier rides
        /// <see cref="SubjectIdentifier.Properties" /> under the key <c>SPProvidedID</c>.
        /// </summary>
        /// <param name="issuerFallback">The assertion issuer, used as the scope when the NameID omits its name qualifier.</param>
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
