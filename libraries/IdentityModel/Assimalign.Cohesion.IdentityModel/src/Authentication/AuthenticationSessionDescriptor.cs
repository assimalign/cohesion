using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Describes the contents of an authentication session before it is materialized into an
/// immutable <see cref="AuthenticationSession" />.
/// </summary>
public class AuthenticationSessionDescriptor
{
    /// <summary>
    /// Gets or sets the session identifier. Required at materialization.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the authenticated subject. Required at
    /// materialization.
    /// </summary>
    public SubjectIdentifier? Subject { get; set; }

    /// <summary>
    /// Gets or sets the kind of the authenticated subject.
    /// </summary>
    public IdentityKind SubjectKind { get; set; }

    /// <summary>
    /// Gets or sets the protocol that authenticated the session.
    /// </summary>
    public AuthenticationProtocol Protocol { get; set; }

    /// <summary>
    /// Gets or sets the asserting provider (SAML IdP entity ID / OIDC issuer). Required for
    /// single-logout correlation.
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// Gets the provider session correlation identifiers (OIDC <c>sid</c> / SAML
    /// <c>SessionIndex</c> values). SAML permits several per session.
    /// </summary>
    public IList<string> ProviderSessionIds { get; } = new List<string>();

    /// <summary>
    /// Gets or sets the authentication context the session was established with.
    /// </summary>
    public AuthenticationContext? Context { get; set; }

    /// <summary>
    /// Gets or sets the instant the session was created. Required at materialization.
    /// </summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the instant the session expires. An expiry at or before
    /// <see cref="CreatedAt" /> is legal (wire-sourced instants such as a SAML
    /// <c>SessionNotOnOrAfter</c> may precede the local completion instant under clock
    /// skew) and simply produces a session that is never active.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Gets or sets the administrative state. Defaults to
    /// <see cref="AuthenticationSessionState.Unknown" />, which is never active — set
    /// <see cref="AuthenticationSessionState.Active" /> explicitly when establishing a
    /// session.
    /// </summary>
    public AuthenticationSessionState State { get; set; }

    /// <summary>
    /// Gets additional session data (for example a tenant scope or correlation
    /// identifiers).
    /// </summary>
    public IDictionary<string, IdentityClaimValue> Properties { get; } =
        new Dictionary<string, IdentityClaimValue>(StringComparer.Ordinal);
}
