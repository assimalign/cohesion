using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Describes the contents of a logout request before it is materialized into a
/// <see cref="ProtocolLogoutRequest" /> derivative.
/// </summary>
public abstract class ProtocolLogoutRequestDescriptor : ProtocolRequestDescriptor
{
    /// <summary>
    /// Gets or sets the identifier of the subject to log out. Nullable: a SAML
    /// <c>EncryptedID</c> cannot be resolved by a descriptive library, and an OpenID
    /// Connect RP-initiated logout may identify the subject only through an unparsed
    /// <c>id_token_hint</c>.
    /// </summary>
    public SubjectIdentifier? Subject { get; set; }

    /// <summary>
    /// Gets the provider session identifiers the logout applies to (SAML
    /// <c>SessionIndex</c> values / OpenID Connect <c>sid</c>). An empty list with a
    /// non-null <see cref="Subject" /> means every session for that principal.
    /// </summary>
    public IList<string> ProviderSessionIds { get; } = new List<string>();

    /// <summary>
    /// Gets or sets why the logout is happening (a SAML <c>Reason</c> URI). This member
    /// is strictly the reason category — subject hints such as the OpenID Connect
    /// <c>logout_hint</c> identify <em>who</em> and belong on branch-derived members.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Gets or sets the instant at or after which the logout request must not be honored
    /// (SAML <c>NotOnOrAfter</c>; the named instant itself is outside the request's
    /// validity).
    /// </summary>
    public DateTimeOffset? NotOnOrAfter { get; set; }
}
