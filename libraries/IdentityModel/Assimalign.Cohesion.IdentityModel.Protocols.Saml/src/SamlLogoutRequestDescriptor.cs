using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Describes the contents of a SAML <c>LogoutRequest</c> before it is materialized into an
/// immutable <see cref="SamlLogoutRequest" />.
/// </summary>
public class SamlLogoutRequestDescriptor : ProtocolLogoutRequestDescriptor
{
    /// <summary>
    /// Gets or sets the wire-faithful NameID identifying the principal to log out.
    /// </summary>
    public SamlNameId? NameId { get; set; }

    /// <summary>
    /// Populates the shared logout members from this request's own NameID and session
    /// indexes: the base subject is derived through the pinned
    /// <see cref="SamlSubjectExtensions.GetSubjectIdentifier(SamlNameId, string?)" /> recipe
    /// (the same one the login leg uses), and the base provider session identifiers are the
    /// request's <c>SessionIndex</c> values.
    /// </summary>
    /// <param name="nameId">The request's NameID.</param>
    /// <param name="sessionIndexes">The request's <c>SessionIndex</c> values, in order. Empty means "all sessions for the principal".</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="nameId" /> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a session index entry is null or whitespace.</exception>
    public void Apply(SamlNameId nameId, IEnumerable<string>? sessionIndexes = null)
    {
        ArgumentNullException.ThrowIfNull(nameId);

        NameId = nameId;
        Subject = nameId.GetSubjectIdentifier(Issuer);

        ProviderSessionIds.Clear();
        if (sessionIndexes is not null)
        {
            foreach (var sessionIndex in sessionIndexes)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(sessionIndex, nameof(sessionIndexes));
                ProviderSessionIds.Add(sessionIndex);
            }
        }
    }
}
